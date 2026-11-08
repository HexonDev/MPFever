using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MPFever
{
    // Differences between two versions of a decompressed savegame, so that a resynchronisation only sends
    // what changed. Both sides keep the same "base" (a previous save of the game).
    //
    // Make (host):
    //  1. cuts both buffers into content-defined chunks (gear hash, FastCDC style) and copies the new chunks
    //     that the base already has (found by a 64-bit hash, then checked byte by byte), each match grown
    //     byte by byte;
    //  2. a gap between two copies that replaces as many bytes of the base becomes a DIFF when enough of
    //     its bytes are unchanged (values changed in place);
    //  3. in the gaps still left, looks for places that resemble the base nearby (after small insertions):
    //     8 equal bytes, grown while equal bytes clearly outnumber different ones;
    //  4. writes the operations:
    //       COPY    base[off, len]: bytes the base has
    //       DIFF    base[off, len]: bytes sent as differences to the base's (mostly zeros: compress well)
    //       LITERAL len: new bytes
    //     The literal bytes and the difference bytes are two streams compressed with Deflate, in 4 MB
    //     segments done in parallel.
    // Apply (player) rebuilds the new data and checks its SHA-256.
    //
    // Layout (little-endian):
    //   0  "MPFD", format version (1 byte), 3 zero bytes
    //   8  base length (int64), 16 new length (int64), 24 SHA-256 of the new data (32 bytes)
    //  56  number of operations, size of the compressed operations, number of literal segments,
    //      number of difference segments (4 x int32)
    //  72  operations (Deflate): varint (length << 2 | kind), then for COPY and DIFF a zigzag varint: base
    //      offset minus the one that keeps the alignment (base - new position) of the previous COPY or DIFF
    //  ..  per segment (literal ones first): size, compressed size (2 x int32)
    //  ..  segments (Deflate), literal ones first; each stream holds its bytes in operation order
    //
    // Thread-safe: no shared mutable state. The operations only depend on the data (fixed gear table);
    // the Deflate bytes may differ between .NET builds, which does not matter to Apply.
    static class SaveDelta
    {
        const int Version = 1, HeaderSize = 72;
        const int OpLiteral = 0, OpCopy = 1, OpDiff = 2;
        const int MaxArray = 0x7FFFFFC7;
        const int SegmentSize = 4 << 20;

        // Tuning (Make only: Apply reads any delta whatever the settings it was made with).
        internal sealed class Options
        {
            public int MinChunk = 256;          // >= 64
            public int AvgBits = 10;            // chunks of about 2^AvgBits bytes
            public int MaxChunk = 64 * 1024;
            public int Normalization = 2;       // FastCDC normalised chunking level (0: none)
            public bool Extend = true;          // grow the matches byte by byte (step 1)
            public int DiffPercent = 25;        // step 2: minimum % of equal bytes (> 100: no step 2)
            public bool Refine = true;          // step 3
            public CompressionLevel Level = CompressionLevel.Optimal;
        }

        internal sealed class Stats
        {
            public int Copies, Diffs, Literals, OpsSize, Segments;
            public long CopyBytes, DiffBytes, LiteralBytes;
        }

        public static byte[] Make(byte[] baseData, byte[] newData)
        {
            Stats st;
            return Make(baseData, newData, new Options(), out st);
        }

        internal static byte[] Make(byte[] baseData, byte[] newData, Options o, out Stats st)
        {
            if (baseData == null) throw new ArgumentNullException(nameof(baseData));
            if (newData == null) throw new ArgumentNullException(nameof(newData));
            if (o.MinChunk < 64 || o.MaxChunk < o.MinChunk || o.AvgBits - o.Normalization < 1 || o.AvgBits + o.Normalization > 63)
                throw new ArgumentException("Bad chunking options.");

            // chunk and hash the base, chunk the new data and hash it whole, at the same time
            int[] baseCuts = null, newCuts = null;
            ulong[] baseKeys = null;
            byte[] sha = null;
            Parallel.Invoke(
                () => { baseCuts = Cut(baseData, o); baseKeys = HashChunks(baseData, baseCuts); },
                () => { newCuts = Cut(newData, o); },
                () => { using (var h = SHA256.Create()) sha = h.ComputeHash(newData); });

            var index = new Dictionary<ulong, int>(baseKeys.Length);
            for (int i = 0; i < baseKeys.Length; i++)
                if (!index.ContainsKey(baseKeys[i])) index.Add(baseKeys[i], i);

            var ops = Match(baseData, newData, baseCuts, baseKeys, index, newCuts, o.Extend);
            if (o.DiffPercent <= 100) MarkDiffs(ops, baseData, newData, o.DiffPercent);
            if (o.Refine) ops = Refine(ops, baseData, newData);

            st = new Stats();
            foreach (var op in ops)
            {
                if (op.Kind == OpCopy) { st.Copies++; st.CopyBytes += op.Len; }
                else if (op.Kind == OpDiff) { st.Diffs++; st.DiffBytes += op.Len; }
                else { st.Literals++; st.LiteralBytes += op.Len; }
            }
            return Write(ops, baseData, newData, sha, o.Level, st);
        }

        public static byte[] Apply(byte[] baseData, byte[] delta)
        {
            if (baseData == null) throw new ArgumentNullException(nameof(baseData));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            if (delta.Length < HeaderSize || delta[0] != 'M' || delta[1] != 'P' || delta[2] != 'F' || delta[3] != 'D')
                throw new InvalidDataException("Not a savegame delta.");
            if (delta[4] != Version) throw new InvalidDataException("Unknown savegame delta version " + delta[4] + ".");
            long baseLen = BitConverter.ToInt64(delta, 8), newLen = BitConverter.ToInt64(delta, 16);
            if (baseLen != baseData.Length) throw new InvalidDataException("The savegame delta was made for another base.");
            if (newLen < 0 || newLen > MaxArray) throw Bad();
            var sha = new byte[32];
            Buffer.BlockCopy(delta, 24, sha, 0, 32);
            int opCount = BitConverter.ToInt32(delta, 56), opsSize = BitConverter.ToInt32(delta, 60);
            int litSegs = BitConverter.ToInt32(delta, 64), diffSegs = BitConverter.ToInt32(delta, 68);
            if (opCount < 0 || opsSize < 0 || litSegs < 0 || diffSegs < 0 || opsSize > delta.Length - HeaderSize
                || (long)litSegs + diffSegs > (delta.Length - HeaderSize - opsSize) / 8)
                throw Bad();

            var result = new byte[newLen];
            try
            {
                byte[] ops;
                using (var z = new DeflateStream(new MemoryStream(delta, HeaderSize, opsSize), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    z.CopyTo(ms);
                    ops = ms.ToArray();
                }

                // segment table
                int segCount = litSegs + diffSegs, tableAt = HeaderSize + opsSize;
                var size = new int[segCount];
                var csize = new int[segCount];
                var start = new int[segCount];
                long at = tableAt + 8L * segCount;
                for (int k = 0; k < segCount; k++)
                {
                    size[k] = BitConverter.ToInt32(delta, tableAt + 8 * k);
                    csize[k] = BitConverter.ToInt32(delta, tableAt + 8 * k + 4);
                    if (size[k] < 0 || size[k] > SegmentSize || csize[k] < 0 || csize[k] > delta.Length - at) throw Bad();
                    start[k] = (int)at;
                    at += csize[k];
                }
                var lit = new DataReader(delta, size, csize, start, 0, litSegs);
                var dif = new DataReader(delta, size, csize, start, litSegs, diffSegs);

                int p = 0, pos = 0;
                long align = 0;     // base - new position of the last COPY or DIFF
                for (int i = 0; i < opCount; i++)
                {
                    ulong h = ReadVarint(ops, ref p);
                    int kind = (int)(h & 3);
                    ulong ulen = h >> 2;
                    if (ulen > (ulong)(newLen - pos)) throw Bad();
                    int len = (int)ulen;
                    if (kind == OpLiteral) lit.Read(result, pos, len);
                    else if (kind == OpCopy || kind == OpDiff)
                    {
                        long off = pos + align + Unzigzag(ReadVarint(ops, ref p));
                        if (off < 0 || off > baseData.Length - len) throw Bad();
                        int b = (int)off;
                        if (kind == OpCopy) Buffer.BlockCopy(baseData, b, result, pos, len);
                        else
                        {
                            dif.Read(result, pos, len);
                            for (int k = 0; k < len; k++) result[pos + k] += baseData[b + k];
                        }
                        align = off - pos;
                    }
                    else throw Bad();
                    pos += len;
                }
                if (pos != newLen) throw Bad();
            }
            catch (IndexOutOfRangeException) { throw Bad(); }

            using (var h = SHA256.Create())
            {
                byte[] got = h.ComputeHash(result);
                for (int i = 0; i < 32; i++)
                    if (got[i] != sha[i]) throw new InvalidDataException("The rebuilt savegame does not match (SHA-256): wrong base?");
            }
            return result;
        }

        static InvalidDataException Bad()
        {
            return new InvalidDataException("Bad savegame delta.");
        }

        // ---- content-defined chunking

        static readonly ulong[] Gear = MakeGear();

        // 256 fixed pseudo-random values (SplitMix64 from a fixed seed): the same on every PC
        static ulong[] MakeGear()
        {
            var g = new ulong[256];
            ulong x = 0x4D50466576657231UL;     // "MPFever1"
            for (int i = 0; i < 256; i++)
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                g[i] = z ^ (z >> 31);
            }
            return g;
        }

        // mask of the k top bits: with h = (h << 1) + Gear[b], the top bits depend on the last 64 bytes
        static ulong TopBits(int k)
        {
            return k <= 0 ? 0 : ~0UL << (64 - k);
        }

        // ends of the chunks of d
        static int[] Cut(byte[] d, Options o)
        {
            var cuts = new List<int>((d.Length >> o.AvgBits) + 16);
            ulong maskS = TopBits(o.AvgBits + o.Normalization), maskL = TopBits(o.AvgBits - o.Normalization);
            int pos = 0;
            while (pos < d.Length)
            {
                pos = NextCut(d, pos, o.MinChunk, 1 << o.AvgBits, o.MaxChunk, maskS, maskL);
                cuts.Add(pos);
            }
            return cuts.ToArray();
        }

        static int NextCut(byte[] d, int start, int min, int avg, int max, ulong maskS, ulong maskL)
        {
            int n = d.Length - start;
            if (n <= min) return d.Length;
            int stop = n > max ? start + max : d.Length;
            int normal = n > avg ? start + avg : d.Length;
            ulong[] g = Gear;
            ulong h = 0;
            // hash the 64 bytes before the minimum size first, so that a cut only depends on the bytes before it
            int i = start + min - 64;
            for (; i < start + min; i++) h = (h << 1) + g[d[i]];
            // harder condition before the average size, easier after (normalised chunking)
            for (; i < normal; i++)
            {
                h = (h << 1) + g[d[i]];
                if ((h & maskS) == 0) return i + 1;
            }
            for (; i < stop; i++)
            {
                h = (h << 1) + g[d[i]];
                if ((h & maskL) == 0) return i + 1;
            }
            return stop;
        }

        static ulong[] HashChunks(byte[] d, int[] cuts)
        {
            var keys = new ulong[cuts.Length];
            int start = 0;
            for (int i = 0; i < cuts.Length; i++)
            {
                keys[i] = Zstd.XXH64(d, start, cuts[i] - start);
                start = cuts[i];
            }
            return keys;
        }

        // ---- step 1: chunks the base has

        struct Op
        {
            public int Kind, Len, Base;     // Base: offset in the base (COPY, DIFF)
        }

        static List<Op> Match(byte[] baseData, byte[] newData, int[] baseCuts, ulong[] baseKeys, Dictionary<ulong, int> index, int[] newCuts, bool extend)
        {
            var ops = new List<Op>();
            int np = 0;             // new data before np is covered by ops
            int lastBaseEnd = 0;    // base end of the last copy
            int s0 = 0;
            for (int j = 0; j < newCuts.Length; j++)
            {
                int s = s0, e = newCuts[j], len = e - s;
                s0 = e;
                if (e <= np) continue;      // already covered by a grown match
                ulong k = Zstd.XXH64(newData, s, len);

                // the base chunk at the place that continues the last copy, else the first one with that hash
                int bi = ChunkAt(baseCuts, lastBaseEnd + (s - np));
                if (bi < 0 || baseKeys[bi] != k)
                {
                    if (!index.TryGetValue(k, out bi)) continue;
                }
                int bs = bi == 0 ? 0 : baseCuts[bi - 1];
                if (baseCuts[bi] - bs != len) continue;

                // check the bytes (the hash has 64 bits only) and grow the match forward
                int same = CommonLength(newData, s, baseData, bs, extend ? int.MaxValue : len);
                if (same < len) continue;
                e = s + same;
                int be = bs + same;
                if (s < np) { bs += np - s; s = np; }   // start of the chunk already covered
                if (extend)
                {
                    while (s > np && bs > 0 && newData[s - 1] == baseData[bs - 1]) { s--; bs--; }
                }
                AddOp(ops, OpLiteral, s - np, 0);
                AddOp(ops, OpCopy, e - s, bs);
                np = e;
                lastBaseEnd = be;
            }
            AddOp(ops, OpLiteral, newData.Length - np, 0);
            return ops;
        }

        // index of the base chunk starting at pos, or -1
        static int ChunkAt(int[] cuts, int pos)
        {
            if (pos == 0) return cuts.Length > 0 ? 0 : -1;
            int i = Array.BinarySearch(cuts, pos);
            return i >= 0 && i + 1 < cuts.Length ? i + 1 : -1;
        }

        // number of equal bytes from a[ai] and b[bi] on, up to max
        static int CommonLength(byte[] a, int ai, byte[] b, int bi, int max)
        {
            max = Math.Min(max, Math.Min(a.Length - ai, b.Length - bi));
            int n = 0;
            while (n + 8 <= max && BitConverter.ToUInt64(a, ai + n) == BitConverter.ToUInt64(b, bi + n)) n += 8;
            while (n < max && a[ai + n] == b[bi + n]) n++;
            return n;
        }

        // appends an operation, merging it with the last one when they continue each other
        static void AddOp(List<Op> ops, int kind, int len, int baseOff)
        {
            if (len <= 0) return;
            if (ops.Count > 0)
            {
                Op last = ops[ops.Count - 1];
                if (last.Kind == kind && (kind == OpLiteral || last.Base + last.Len == baseOff))
                {
                    last.Len += len;
                    ops[ops.Count - 1] = last;
                    return;
                }
            }
            ops.Add(new Op { Kind = kind, Len = len, Base = baseOff });
        }

        // ---- step 2: gaps that replace as many bytes of the base

        static void MarkDiffs(List<Op> ops, byte[] baseData, byte[] newData, int percent)
        {
            int pos = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                Op op = ops[i];
                if (op.Kind == OpLiteral)
                {
                    // the base range between the copies around the gap (or the ends of the base)
                    int from = i == 0 ? 0 : ops[i - 1].Base + ops[i - 1].Len;
                    int to = i + 1 < ops.Count ? ops[i + 1].Base : baseData.Length;
                    if (to - from == op.Len)
                    {
                        int same = 0;
                        for (int k = 0; k < op.Len; k++)
                            if (newData[pos + k] == baseData[from + k]) same++;
                        if ((long)same * 100 >= (long)percent * op.Len)
                        {
                            op.Kind = OpDiff;
                            op.Base = from;
                            ops[i] = op;
                        }
                    }
                }
                pos += op.Len;
            }
        }

        // ---- step 3: places in the gaps that resemble the base nearby

        const int Margin = 4096;            // base bytes indexed around a gap's expected place
        const int WindowCap = 2 << 20;      // and at most this many from each end
        const int MinRegion = 128;          // shorter regions stay literal
        const int Bridge = 128;             // up to this many bytes between two regions at the same place join them
        const int Penalty = 7;              // a different byte cancels this many equal ones
        const int TableBits = 20, Step = 4;
        const ulong HashMul = 0x9E3779B97F4A7C15UL;

        static List<Op> Refine(List<Op> ops, byte[] b, byte[] n)
        {
            var res = new List<Op>(ops.Count * 2);
            // 8-byte base words by hash: check bits (top 32 bits of the hash) | position + 1. Never cleared:
            // an older entry is still a place of the base, and every candidate is checked.
            ulong[] table = null;
            int pos = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                Op op = ops[i];
                if (op.Kind != OpLiteral || op.Len < MinRegion)
                {
                    AddOp(res, op.Kind, op.Len, op.Base);
                    pos += op.Len;
                    continue;
                }
                if (table == null) table = new ulong[1 << TableBits];
                // index the base after the previous copy and before the next one
                int from = i == 0 ? 0 : ops[i - 1].Base + ops[i - 1].Len;
                int to = i + 1 < ops.Count ? ops[i + 1].Base : b.Length;
                int span = Math.Min(op.Len, WindowCap);
                int lo1 = Math.Max(0, from - Margin), hi1 = Math.Min(b.Length, from + span + Margin);
                int lo2 = Math.Max(0, to - span - Margin), hi2 = Math.Min(b.Length, to + Margin);
                if (lo2 <= hi1 && lo1 <= hi2) { lo1 = Math.Min(lo1, lo2); hi1 = Math.Max(hi1, hi2); lo2 = hi2 = 0; }
                IndexRange(b, lo1, hi1, table);
                IndexRange(b, lo2, hi2, table);
                RefineGap(res, b, n, pos, pos + op.Len, from - pos, table);
                pos += op.Len;
            }
            return res;
        }

        // one base position in Step: the scan of the new data tries every position, so it still finds them
        static void IndexRange(byte[] b, int lo, int hi, ulong[] table)
        {
            for (int q = lo - lo % Step; q + 8 <= hi; q += Step)
            {
                ulong h = BitConverter.ToUInt64(b, q) * HashMul;
                table[(int)(h >> (64 - TableBits))] = (h & 0xFFFFFFFF00000000UL) | (uint)(q + 1);
            }
        }

        // Splits the literal gap new[gs, ge) into DIFF regions and literals.
        // off: base position - new position of the last region (at first, of the copy before the gap).
        static void RefineGap(List<Op> res, byte[] b, byte[] n, int gs, int ge, int off, ulong[] table)
        {
            int p = gs, last = gs;
            while (p + 8 <= ge)
            {
                // 8 equal bytes: at the place that continues the last region, else where the index says
                ulong w = BitConverter.ToUInt64(n, p);
                int q = p + off;
                if (q < 0 || q > b.Length - 8 || BitConverter.ToUInt64(b, q) != w)
                {
                    ulong h = w * HashMul;
                    ulong e = table[(int)(h >> (64 - TableBits))];
                    q = (int)(uint)e - 1;
                    if (q < 0 || (e ^ h) >> 32 != 0 || BitConverter.ToUInt64(b, q) != w) { p++; continue; }
                }
                // grow forward while equal bytes clearly outnumber different ones, keep the best end
                int score = 0, best = 0, fwd = 0;
                for (int k = 0; p + k < ge && q + k < b.Length; k++)
                {
                    if (n[p + k] == b[q + k]) { if (++score > best) { best = score; fwd = k + 1; } }
                    else if ((score -= Penalty) < best - 64) break;
                }
                // and backward over the literal bytes before it
                int back = 0;
                score = 0; best = 0;
                for (int k = 1; p - k >= last && q - k >= 0; k++)
                {
                    if (n[p - k] == b[q - k]) { if (++score > best) { best = score; back = k; } }
                    else if ((score -= Penalty) < best - 64) break;
                }
                if (fwd + back < MinRegion) { p += Math.Max(1, fwd - 7); continue; }
                int start = p - back;
                if (q - p == off && start - last <= Bridge) AddOp(res, OpDiff, start - last, last + off);
                else AddOp(res, OpLiteral, start - last, 0);
                AddOp(res, OpDiff, fwd + back, q - back);
                last = p + fwd;
                off = q - p;
                p = last;
            }
            AddOp(res, OpLiteral, ge - last, 0);
        }

        // ---- delta file

        static byte[] Write(List<Op> ops, byte[] baseData, byte[] newData, byte[] sha, CompressionLevel level, Stats st)
        {
            // operations
            var opBytes = new MemoryStream();
            long align = 0;
            int pos = 0;
            foreach (var op in ops)
            {
                WriteVarint(opBytes, (ulong)op.Len << 2 | (uint)op.Kind);
                if (op.Kind != OpLiteral)
                {
                    WriteVarint(opBytes, Zigzag(op.Base - (pos + align)));
                    align = op.Base - pos;
                }
                pos += op.Len;
            }
            byte[] opsZ = Deflate(opBytes.GetBuffer(), (int)opBytes.Length, level);

            // two streams: the literal bytes (0) and the difference bytes (1), with, for each operation that
            // carries bytes, where they start in its stream
            var newPos = new int[ops.Count];
            var carriers = new[] { new List<int>(), new List<int>() };
            var starts = new[] { new List<long>(), new List<long>() };
            var length = new long[2];
            pos = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                newPos[i] = pos;
                int s = ops[i].Kind == OpLiteral ? 0 : ops[i].Kind == OpDiff ? 1 : -1;
                if (s >= 0)
                {
                    carriers[s].Add(i);
                    starts[s].Add(length[s]);
                    length[s] += ops[i].Len;
                }
                pos += ops[i].Len;
            }
            long[][] startArr = { starts[0].ToArray(), starts[1].ToArray() };

            // segments of both streams, compressed in parallel
            int litSegs = (int)((length[0] + SegmentSize - 1) / SegmentSize);
            int diffSegs = (int)((length[1] + SegmentSize - 1) / SegmentSize);
            var segs = new byte[litSegs + diffSegs][];
            var segLen = new int[segs.Length];
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
            Parallel.For(0, segs.Length, po, k =>
            {
                int s = k < litSegs ? 0 : 1;
                long s0 = (long)(k - (s == 0 ? 0 : litSegs)) * SegmentSize;
                int len = (int)Math.Min(SegmentSize, length[s] - s0);
                var buf = new byte[len];
                long[] at = startArr[s];
                int i = Array.BinarySearch(at, s0);
                if (i < 0) i = ~i - 1;
                for (int w = 0; w < len; i++)
                {
                    int oi = carriers[s][i];
                    Op op = ops[oi];
                    int inOp = (int)(s0 + w - at[i]);
                    int take = Math.Min(op.Len - inOp, len - w);
                    int np = newPos[oi] + inOp;
                    if (s == 0) Buffer.BlockCopy(newData, np, buf, w, take);
                    else
                    {
                        int bp = op.Base + inOp;
                        for (int x = 0; x < take; x++) buf[w + x] = (byte)(newData[np + x] - baseData[bp + x]);
                    }
                    w += take;
                }
                segLen[k] = len;
                segs[k] = Deflate(buf, len, level);
            });

            long total = HeaderSize + opsZ.Length + 8L * segs.Length;
            foreach (var z in segs) total += z.Length;
            if (total > MaxArray) throw new InvalidOperationException("Savegame delta too large.");
            var res = new byte[total];
            res[0] = (byte)'M'; res[1] = (byte)'P'; res[2] = (byte)'F'; res[3] = (byte)'D';
            res[4] = Version;
            PutInt64(res, 8, baseData.Length);
            PutInt64(res, 16, newData.Length);
            Buffer.BlockCopy(sha, 0, res, 24, 32);
            PutInt32(res, 56, ops.Count);
            PutInt32(res, 60, opsZ.Length);
            PutInt32(res, 64, litSegs);
            PutInt32(res, 68, diffSegs);
            int p = HeaderSize;
            Buffer.BlockCopy(opsZ, 0, res, p, opsZ.Length);
            p += opsZ.Length;
            for (int k = 0; k < segs.Length; k++)
            {
                PutInt32(res, p, segLen[k]);
                PutInt32(res, p + 4, segs[k].Length);
                p += 8;
            }
            foreach (var z in segs)
            {
                Buffer.BlockCopy(z, 0, res, p, z.Length);
                p += z.Length;
            }

            st.OpsSize = opsZ.Length;
            st.Segments = segs.Length;
            return res;
        }

        static byte[] Deflate(byte[] d, int len, CompressionLevel level)
        {
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, level, true)) z.Write(d, 0, len);
            return ms.ToArray();
        }

        // reads one stream across its segments
        sealed class DataReader
        {
            readonly byte[] delta;
            readonly int[] size, csize, start;
            readonly int end;
            int seg, pos, len;
            byte[] buf = new byte[0];

            public DataReader(byte[] delta, int[] size, int[] csize, int[] start, int first, int count)
            {
                this.delta = delta; this.size = size; this.csize = csize; this.start = start;
                seg = first - 1;
                end = first + count;
            }

            public void Read(byte[] dst, int off, int count)
            {
                while (count > 0)
                {
                    if (pos == len) Next();
                    int n = Math.Min(count, len - pos);
                    Buffer.BlockCopy(buf, pos, dst, off, n);
                    pos += n; off += n; count -= n;
                }
            }

            void Next()
            {
                if (++seg >= end) throw Bad();
                if (buf.Length < size[seg]) buf = new byte[size[seg]];
                using (var z = new DeflateStream(new MemoryStream(delta, start[seg], csize[seg]), CompressionMode.Decompress))
                    ReadFully(z, buf, 0, size[seg]);
                pos = 0;
                len = size[seg];
            }
        }

        static void PutInt64(byte[] b, int at, long v)
        {
            for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static void PutInt32(byte[] b, int at, int v)
        {
            for (int i = 0; i < 4; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static void WriteVarint(Stream s, ulong v)
        {
            while (v >= 0x80)
            {
                s.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            s.WriteByte((byte)v);
        }

        static ulong ReadVarint(byte[] b, ref int p)
        {
            ulong v = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                byte x = b[p++];
                v |= (ulong)(x & 0x7F) << shift;
                if (x < 0x80) return v;
            }
            throw Bad();
        }

        static ulong Zigzag(long v) { return (ulong)((v << 1) ^ (v >> 63)); }
        static long Unzigzag(ulong v) { return (long)(v >> 1) ^ -(long)(v & 1); }

        static void ReadFully(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, off, count);
                if (n <= 0) throw new InvalidDataException("Savegame delta truncated.");
                off += n;
                count -= n;
            }
        }
    }
}
