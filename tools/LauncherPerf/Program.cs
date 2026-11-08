using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

// Loads each build separately; measured calls use bound delegates, not MethodInfo.Invoke.
// No game, sockets, NuGet packages, or changes to the launcher's public API are needed.
static class Program
{
    static long sink;
    static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    sealed class Launcher
    {
        readonly Type reader;
        public readonly Func<byte[], byte[], byte[]> Make, Apply;
        public readonly Func<byte[], int, byte[], int, int, int> Prefix;
        public readonly Func<string, object> Parse;

        public Launcher(string path)
        {
            var context = new AssemblyLoadContext(Path.GetFullPath(path), isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            Type T(string name) => assembly.GetType("MPFever." + name, throwOnError: true)!;
            var delta = T("SaveDelta");
            Make = delta.GetMethod("Make", Static, new[] { typeof(byte[]), typeof(byte[]) })!
                .CreateDelegate<Func<byte[], byte[], byte[]>>();
            Apply = delta.GetMethod("Apply", Static)!.CreateDelegate<Func<byte[], byte[], byte[]>>();
            Prefix = delta.GetMethod("CommonLength", Static)!
                .CreateDelegate<Func<byte[], int, byte[], int, int, int>>();
            Parse = T("LuaLit").GetMethod("Parse", Static)!.CreateDelegate<Func<string, object>>();
            reader = T("NetReader");
        }

        public (Func<string?> Line, Func<int, byte[]> Bytes) Reader(Stream stream)
        {
            var instance = Activator.CreateInstance(reader, stream)!;
            return (reader.GetMethod("ReadLine")!.CreateDelegate<Func<string?>>(instance),
                reader.GetMethod("ReadBytes")!.CreateDelegate<Func<int, byte[]>>(instance));
        }
    }

    sealed class ShortReads : MemoryStream
    {
        readonly int maxRead;
        public ShortReads(byte[] bytes, int maxRead) : base(bytes, writable: false) { this.maxRead = maxRead; }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maxRead));
    }

    sealed record Sample(string Build, string Workload, int Iterations, double MedianMs, long MedianAllocatedBytes);

    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void CheckReader(Launcher launcher)
    {
        var binary = new byte[] { 0, 10, 13, 255, 195, 0 };
        var first = Encoding.UTF8.GetBytes("\n\r\nárvíztűrő tükörfúrógép 🚂\r\r\n");
        var bytes = first.Concat(binary).Concat(Encoding.UTF8.GetBytes("next\nunfinished")).ToArray();
        foreach (int fragment in new[] { 1, 2, 7, 65536 })
        {
            using var stream = new ShortReads(bytes, fragment);
            var reader = launcher.Reader(stream);
            Check(reader.Line() == "" && reader.Line() == "", "empty LF/CRLF lines");
            Check(reader.Line() == "árvíztűrő tükörfúrógép 🚂\r", "UTF-8 across reads; remove exactly one CR");
            Check(reader.Bytes(binary.Length).AsSpan().SequenceEqual(binary), "line reader must preserve buffered binary bytes");
            Check(reader.Line() == "next" && reader.Line() == null && reader.Line() == null, "discard an unfinished final line");
        }
        foreach (int length in new[] { 65534, 65535, 65536, 65537, 850000 })
        {
            string line = new('x', length);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\r\nend\n"));
            var reader = launcher.Reader(stream);
            Check(reader.Line() == line && reader.Line() == "end" && reader.Line() == null, "64 KiB boundary / large line");
        }
        using (var stream = new ShortReads(new byte[] { 1, 2 }, 1))
        {
            bool refused = false;
            try { launcher.Reader(stream).Bytes(3); }
            catch (IOException) { refused = true; }
            Check(refused, "refuse a truncated binary payload");
        }
    }

    static void CheckPrefix(Launcher launcher)
    {
        var random = new Random(178);
        for (int trial = 0; trial < 1200; trial++)
        {
            var a = new byte[random.Next(0, 2048)];
            random.NextBytes(a);
            int ai = random.Next(a.Length + 1), bi = random.Next(32);
            var b = new byte[bi + a.Length - ai + random.Next(32)];
            a.AsSpan(ai).CopyTo(b.AsSpan(bi));
            int available = Math.Min(a.Length - ai, b.Length - bi);
            if (available > 0 && trial % 2 == 0) b[bi + random.Next(available)] ^= 1;
            int max = trial % 3 == 0 ? int.MaxValue : random.Next(available + 1);
            int expected = 0;
            while (expected < max && expected < available && a[ai + expected] == b[bi + expected]) expected++;
            Check(launcher.Prefix(a, ai, b, bi, max) == expected, "prefix bounds, offsets, mismatch and empty spans");
        }
    }

    static (byte[] Base, byte[] Sparse, byte[] Widespread) Saves()
    {
        var original = new byte[12 << 20];
        new Random(94).NextBytes(original);
        // Records with both variable and repeated fields, rather than an all-zero or random-only save.
        for (int i = 0; i < original.Length; i += 256) original.AsSpan(i + 64, 192).Clear();
        var sparse = new byte[original.Length + 137];
        int middle = original.Length / 2;
        original.AsSpan(0, middle).CopyTo(sparse);
        new Random(95).NextBytes(sparse.AsSpan(middle, 137));
        original.AsSpan(middle).CopyTo(sparse.AsSpan(middle + 137));
        for (int i = 0; i + 512 <= sparse.Length; i += 65536)
            for (int j = 0; j < 512; j++) sparse[i + j] ^= 0x5a;
        var widespread = (byte[])original.Clone();
        for (int i = 0; i < widespread.Length; i += 8) widespread[i] ^= 0x5a;
        return (original, sparse, widespread);
    }

    static void CheckDelta(Launcher before, Launcher after, byte[] basis, byte[] changed)
    {
        byte[] oldDelta = before.Make(basis, changed), newDelta = after.Make(basis, changed);
        Check(after.Apply(basis, oldDelta).AsSpan().SequenceEqual(changed), "candidate reads baseline delta");
        Check(before.Apply(basis, newDelta).AsSpan().SequenceEqual(changed), "baseline reads candidate delta");
        // Same runtime, settings and input: pooling must not leak unused bytes into the compressed segments.
        Check(oldDelta.AsSpan().SequenceEqual(newDelta), "delta bytes and size unchanged");
        var corrupt = (byte[])newDelta.Clone();
        corrupt[24] ^= 1;
        bool refused = false;
        try { after.Apply(basis, corrupt); }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "SHA-256 validation still rejects a corrupt delta");
    }

    static Sample Measure(string build, string name, int iterations, Func<long> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int i = 0; i < 3; i++) sink ^= action();
        var elapsed = new List<double>();
        var allocated = new List<long>();
        for (int round = 0; round < 7; round++)
        {
            long startBytes = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) sink ^= action();
            elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations);
            allocated.Add((GC.GetTotalAllocatedBytes(precise: true) - startBytes) / iterations);
        }
        elapsed.Sort(); allocated.Sort();
        var sample = new Sample(build, name, iterations, elapsed[3], allocated[3]);
        Console.WriteLine($"{build,-10} {name,-22} {sample.MedianMs,10:F3} ms {sample.MedianAllocatedBytes,14:N0} B/op");
        return sample;
    }

    static long Lines(Launcher launcher, byte[] input, int fragment)
    {
        using var stream = new ShortReads(input, fragment);
        var reader = launcher.Reader(stream);
        long size = 0;
        for (string? line; (line = reader.Line()) != null;) size += line.Length;
        return size;
    }

    static int Main(string[] args)
    {
        try
        {
            string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string candidatePath = Arg("--candidate") ?? "launcher/bin/Release/MPFever.dll";
            string baselinePath = Arg("--baseline") ?? candidatePath;
            var before = new Launcher(baselinePath);
            var after = new Launcher(candidatePath);
            var saves = Saves();
            foreach (var launcher in new[] { before, after }) { CheckReader(launcher); CheckPrefix(launcher); }
            CheckDelta(before, after, Array.Empty<byte>(), Array.Empty<byte>());
            CheckDelta(before, after, new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3, 4 });
            CheckDelta(before, after, new byte[] { 1, 2, 3 }, Array.Empty<byte>());
            CheckDelta(before, after, saves.Base, saves.Base);
            CheckDelta(before, after, saves.Base, saves.Sparse);
            CheckDelta(before, after, saves.Base, saves.Widespread);
            var literal = new byte[(4 << 20) + 37];
            new Random(96).NextBytes(literal);
            CheckDelta(before, after, Array.Empty<byte>(), literal);
            Console.WriteLine("PASS: reader boundaries, fragmented UTF-8/binary, prefixes, cross-build delta compatibility and corruption checks");
            if (args.Contains("--checks-only")) return 0;

            byte[] shortLines = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("clock\tHost\t{[\"time\"]=123456}\n", 20000)));
            byte[] largeLines = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('x', 850000) + "\n", 12)));
            byte[] fragmented = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('é', 512) + "\r\n", 500)));
            string numbers = "{" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"[{i}]={i}.125")) + "}";
            byte[] prefixA = new byte[65536], prefixB = new byte[65536];
            new Random(97).NextBytes(prefixA); prefixA.CopyTo(prefixB, 0);
            prefixB[^1] ^= 1;
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; CPUs: {Environment.ProcessorCount}");
            Console.WriteLine("Median of 7 samples after warmup; allocations are process-wide, including delta worker threads.");
            var results = new List<Sample>();
            // Alternate build order across workloads to reduce systematic warmup/temperature bias.
            var jobs = new (string Name, int Iterations, Func<Launcher, long> Run)[]
            {
                ("lines-20k", 5, l => Lines(l, shortLines, 65536)),
                ("lines-850k-x12", 2, l => Lines(l, largeLines, 65536)),
                ("lines-fragmented-37B", 3, l => Lines(l, fragmented, 37)),
                ("lua-1000-numbers", 20, l => ((System.Collections.IDictionary)l.Parse(numbers)).Count),
                ("prefix-64KiB-x4000", 2, l => { long n = 0; for (int i = 0; i < 4000; i++) n += l.Prefix(prefixA, 0, prefixB, 0, int.MaxValue); return n; }),
                ("delta-sparse-12MiB", 1, l => l.Make(saves.Base, saves.Sparse).Length),
                ("delta-widespread-12MiB", 1, l => l.Make(saves.Base, saves.Widespread).Length)
            };
            for (int j = 0; j < jobs.Length; j++)
            {
                var job = jobs[j];
                if (Arg("--filter") is string filter && !job.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (name, launcher) in j % 2 == 0
                    ? new[] { ("baseline", before), ("candidate", after) }
                    : new[] { ("candidate", after), ("baseline", before) })
                    results.Add(Measure(name, job.Name, job.Iterations, () => job.Run(launcher)));
            }
            if (Arg("--json") is string json)
                File.WriteAllText(json, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            GC.KeepAlive(sink);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
