using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace MPFever
{
    // Opt-in transport experiment only. No multiplayer messages travel over this pipe yet.
    // Byte stream: uint32 LE body length (8), uint32 LE ticket, four ASCII bytes ping/pong.
    sealed class PipeProbe : IDisposable
    {
        public readonly string Name = "mpfever-probe-" + Guid.NewGuid().ToString("N");
        readonly NamedPipeServerStream pipe;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly Action<uint> pong;
        readonly Action<string> log;
        public Task Completion { get; }

        public PipeProbe(Action<string> log, Action<uint> pong = null)
        {
            this.log = log;
            this.pong = pong;
            pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            Completion = Run(stop.Token);
        }

        async Task Run(CancellationToken token)
        {
            var frame = new byte[12];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                        log?.Invoke("pipe probe: native client connected");
                        while (!token.IsCancellationRequested)
                        {
                            // Also bounds partial frames and a connected client that never sends anything.
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(30));
                            await pipe.ReadExactlyAsync(frame.AsMemory(0, 4), deadline.Token).ConfigureAwait(false);
                            if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != 8)
                                throw new InvalidDataException("unexpected frame length");
                            await pipe.ReadExactlyAsync(frame.AsMemory(4, 8), deadline.Token).ConfigureAwait(false);
                            uint ticket = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4));
                            if (ticket == 0 || frame[8] != 'p' || frame[9] != 'i' || frame[10] != 'n' || frame[11] != 'g')
                                throw new InvalidDataException("expected a ping and a nonzero ticket");
                            frame[9] = (byte)'o';
                            await pipe.WriteAsync(frame.AsMemory(), deadline.Token).ConfigureAwait(false);
                            pong?.Invoke(ticket);
                            log?.Invoke("pipe probe: ping #" + ticket + " -> pong");
                        }
                    }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is OperationCanceledException)
                    {
                        if (!token.IsCancellationRequested) log?.Invoke("pipe probe: client closed (" + e.Message + ")");
                    }
                    finally
                    {
                        if (!token.IsCancellationRequested && pipe.IsConnected) pipe.Disconnect();
                    }
                }
            }
            catch (Exception e) when (token.IsCancellationRequested && (e is ObjectDisposedException || e is IOException || e is OperationCanceledException)) { }
        }

        public void Dispose()
        {
            // Do not wait on the UI thread. Closing the handle also releases a pending connect/read/write.
            stop.Cancel();
            pipe.Dispose();
        }
    }
}
