using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Rooms;
using Hayabusa.SampleServer;

namespace Hayabusa.Benchmarks;

#pragma warning disable CA1852 // BenchmarkDotNet requires non-sealed benchmark classes
#pragma warning disable CA1063 // BenchmarkDotNet simple IDisposable cleanup
#pragma warning disable CA1816 // BenchmarkDotNet simple IDisposable cleanup
#pragma warning disable CA1822 // BenchmarkDotNet requires instance methods
#pragma warning disable CA2012 // Synchronous benchmark runner consumes completed in-memory ValueTask

/// <summary>
/// BenchmarkDotNet suite covering all 4 mandatory Hayabusa hot-path targets and Kestrel / ASP.NET Core / JSON baselines:
/// 1. Frame Parse + HTTP/RPC Routing (0 B allocation) vs PipeReader + Dynamic Route Dictionary
/// 2. MemoryPack Unary RPC Struct Roundtrip (0 B allocation) vs System.Text.Json + Task&lt;T&gt;
/// 3. Room Broadcast (1 Room x 100 Players x 60fps Zero-Copy Shared Reference, 0 B allocation) vs Per-Player Task/Array Copy
/// 4. LogicLooper Tick Scheduler across 1,000 Rooms (0 B allocation)
/// </summary>
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class HayabusaHotPathBenchmarks : IDisposable
{
    private readonly PooledBufferWriter _writer = new(64 * 1024);
    private readonly HayabusaContext _context;
    private readonly RpcDispatcher _dispatcher = new();
    private readonly TransportSubmissionQueue _submissionQueue = new(1024);
    private readonly RoomManager _broadcastRoomManager;
    private readonly BattleRoomService _broadcastRoom = new();
    private readonly RoomManager _thousandRoomManager;
    private readonly byte[] _sampleRpcRequestFrame;
    private readonly byte[] _sampleHttpRequestBytes;
    private readonly byte[] _pipelined16HttpRequestBytes;
    private readonly byte[] _sampleJsonRequestBytes;
    private readonly ContiguousReceiveBuffer _pipelineReceiveBuffer = new(64 * 1024);
    private readonly Dictionary<string, Func<string>> _aspNetStyleRouteTable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="HayabusaHotPathBenchmarks"/> class and pre-warms buffers.
    /// </summary>
    public HayabusaHotPathBenchmarks()
    {
        _context = new HayabusaContext(CoreIndex.Zero, _writer);
        _dispatcher.MapRpc<MoveCommand, WorldSnapshot>(
            1,
            static (ctx, _) => new WorldSnapshot(ctx.CurrentTick, 1));
        _dispatcher.MapHttp(HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        _aspNetStyleRouteTable["/health"] = static () => "OK";

        var seedWriter = new PooledBufferWriter(256);
        var move = new MoveCommand(10.5f, 20.0f, -3.25f);
        FrameParser.WriteMemoryPackFrame(ref seedWriter, new MethodId(1), RpcFrameFlags.None, in move);
        _sampleRpcRequestFrame = seedWriter.WrittenSpan.ToArray();
        seedWriter.Dispose();

        _sampleHttpRequestBytes = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
        _pipelined16HttpRequestBytes = new byte[_sampleHttpRequestBytes.Length * 16];
        for (int i = 0; i < 16; i++)
        {
            _sampleHttpRequestBytes.AsSpan().CopyTo(_pipelined16HttpRequestBytes.AsSpan(i * _sampleHttpRequestBytes.Length));
        }

        _sampleJsonRequestBytes = JsonSerializer.SerializeToUtf8Bytes(move);

        _broadcastRoomManager = new RoomManager(CoreIndex.Zero, _context, _submissionQueue);
        _broadcastRoomManager.RegisterRoom(new RoomId(1UL), _broadcastRoom);
        for (ulong i = 1; i <= 100; i++)
        {
            _broadcastRoom.JoinPlayer(new ConnectionId(i));
        }

        _thousandRoomManager = new RoomManager(CoreIndex.Zero, _context, _submissionQueue);
        for (ulong i = 1; i <= 1000; i++)
        {
            _ = _thousandRoomManager.CreateAndRegisterRoom<LightweightBenchmarkRoom>(new RoomId(i));
        }
    }

    /// <summary>Benchmark 1 (Hayabusa): Binary Frame Parse + HTTP Request Parse &amp; Route Dispatch (0 B target).</summary>
    [Benchmark]
    public bool FrameParseAndHttpRouting()
    {
        _writer.Clear();
        HttpParseError err = HttpParser.TryParseRequest(_sampleHttpRequestBytes, out HttpRequestView req);
        if (err != HttpParseError.None)
        {
            return false;
        }

        return _dispatcher.DispatchHttp(_context, in req, _writer);
    }

    /// <summary>Baseline 1 (ASP.NET / PipeReader style): Pipe writing, string decoding, and dictionary routing.</summary>
    [Benchmark]
    public async ValueTask<int> BaselinePipeReaderAndStringHttpRouting()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(_sampleHttpRequestBytes);
        await pipe.Writer.CompleteAsync();

        ReadResult read = await pipe.Reader.ReadAsync();
        string raw = Encoding.UTF8.GetString(read.Buffer);
        pipe.Reader.AdvanceTo(read.Buffer.End);

        int firstSpace = raw.IndexOf(' ', StringComparison.Ordinal);
        int secondSpace = raw.IndexOf(' ', firstSpace + 1);
        string path = raw.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
        string body = _aspNetStyleRouteTable.TryGetValue(path, out Func<string>? fn) ? fn() : "NotFound";
        byte[] responseBytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        return responseBytes.Length;
    }

    /// <summary>Benchmark 2 (Hayabusa): MemoryPack Unary RPC Frame Parse + Deserialize + Handler + Serialize (0 B target).</summary>
    [Benchmark]
    public bool MemoryPackUnaryRpcEcho()
    {
        _writer.Clear();
        FrameParseError err = FrameParser.TryParse(_sampleRpcRequestFrame, RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err != FrameParseError.None)
        {
            return false;
        }

        return _dispatcher.TryDispatchSync(_context, in frame, _writer);
    }

    /// <summary>Baseline 2 (ASP.NET Core JSON / Task style): System.Text.Json Deserialize + Task handler + Serialize.</summary>
    [Benchmark]
    public async Task<int> BaselineSystemTextJsonAndTaskRpcEcho()
    {
        MoveCommand cmd = JsonSerializer.Deserialize<MoveCommand>(_sampleJsonRequestBytes);
        WorldSnapshot snapshot = await Task.Run(() => new WorldSnapshot(new TickNumber(1UL), cmd.X >= 0 ? 1 : 0));
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return response.Length;
    }

    /// <summary>Benchmark 3 (Hayabusa): 1 Room x 100 Players Broadcast using 1 serialization + zero-copy shared slice (0 B target).</summary>
    [Benchmark]
    public int RoomBroadcast100Players()
    {
        _submissionQueue.Clear();
        var snapshot = new WorldSnapshot(new TickNumber(42UL), 100);
        return _broadcastRoom.BroadcastSnapshot(new MethodId(100), in snapshot);
    }

    /// <summary>Baseline 3 (Standard WebSocket/SignalR style): Per-player JSON serialization and Task.WhenAll fan-out for 100 players.</summary>
    [Benchmark]
    public async Task<int> BaselineTaskWhenAllJsonBroadcast100Players()
    {
        var snapshot = new WorldSnapshot(new TickNumber(42UL), 100);
        var tasks = new Task<int>[100];
        for (int i = 0; i < 100; i++)
        {
            tasks[i] = Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(snapshot).Length);
        }

        int[] lengths = await Task.WhenAll(tasks);
        return lengths.Length;
    }

    /// <summary>Benchmark 4 (Hayabusa): Tick 1,000 Rooms on a single core worker (0 B target).</summary>
    [Benchmark]
    public int Tick1000Rooms()
    {
        _thousandRoomManager.TickAllRooms(new TickNumber(1UL));
        return _thousandRoomManager.ActiveCount;
    }

    /// <summary>Benchmark 5 (Hayabusa Kestrel-Killer): 16x HTTP/1.1 Pipelined Plaintext Batch Parse + Dispatch + Coalesced Response (0 B target).</summary>
    [Benchmark]
    public int Http16PipelinedBatchPlaintext()
    {
        _writer.Clear();
        _pipelineReceiveBuffer.Reset();
        _pipelineReceiveBuffer.TryAppend(_pipelined16HttpRequestBytes);

        int processed = 0;
        while (_pipelineReceiveBuffer.ReadableBytes > 0)
        {
            HttpParseError err = HttpParser.TryParseRequest(_pipelineReceiveBuffer.ReadableSpan, out HttpRequestView req);
            if (err != HttpParseError.None)
            {
                break;
            }

            _context.Bind(new ConnectionId(1UL), RoomId.Invalid, TickNumber.Zero, clearWriter: false);
            _dispatcher.DispatchHttp(_context, in req, _writer);
            _pipelineReceiveBuffer.Consume(req.ConsumedBytes);
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Runs a quick deterministic in-process comparison summary when <c>--quick-compare</c> is passed.
    /// </summary>
    public static void RunQuickComparison(int iterations = 100_000)
    {
        using var bench = new HayabusaHotPathBenchmarks();

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            _ = bench.FrameParseAndHttpRouting();
            _ = bench.BaselinePipeReaderAndStringHttpRouting().GetAwaiter().GetResult();
            _ = bench.Http16PipelinedBatchPlaintext();
            _ = bench.MemoryPackUnaryRpcEcho();
            _ = bench.BaselineSystemTextJsonAndTaskRpcEcho().GetAwaiter().GetResult();
            _ = bench.RoomBroadcast100Players();
            _ = bench.BaselineTaskWhenAllJsonBroadcast100Players().GetAwaiter().GetResult();
            _ = bench.Tick1000Rooms();
        }

        Measure("1. HTTP Parse + Routing (Hayabusa)", iterations, () => bench.FrameParseAndHttpRouting());
        Measure("1. HTTP Parse + Routing (PipeReader + String Baseline)", iterations, () => bench.BaselinePipeReaderAndStringHttpRouting().GetAwaiter().GetResult());
        Measure("2. HTTP 16x Pipelined Batch (Hayabusa Coalesced)", iterations / 2, () => bench.Http16PipelinedBatchPlaintext());
        Measure("3. Unary RPC Echo (Hayabusa MemoryPack)", iterations, () => bench.MemoryPackUnaryRpcEcho());
        Measure("3. Unary RPC Echo (System.Text.Json + Task Baseline)", iterations / 10, () => bench.BaselineSystemTextJsonAndTaskRpcEcho().GetAwaiter().GetResult());
        Measure("4. Room Broadcast 100 Players (Hayabusa Zero-Copy)", iterations, () => bench.RoomBroadcast100Players());
        Measure("4. Room Broadcast 100 Players (Task.WhenAll + JSON Baseline)", iterations / 10, () => bench.BaselineTaskWhenAllJsonBroadcast100Players().GetAwaiter().GetResult());
        Measure("5. Tick 1,000 Rooms (Hayabusa LogicLooper)", iterations / 10, () => bench.Tick1000Rooms());
    }

    private static void Measure<T>(string name, int iterations, Func<T> action)
    {
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            _ = action();
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        double nsPerOp = elapsed.TotalNanoseconds / iterations;
        double opsPerSec = iterations / elapsed.TotalSeconds;
        long bytesPerOp = (allocAfter - allocBefore) / iterations;
        Console.WriteLine($"{name,-58} | {nsPerOp,8:F1} ns/op | {opsPerSec,13:N0} ops/s | {bytesPerOp,6} B/op");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _thousandRoomManager.Dispose();
        _broadcastRoomManager.Dispose();
        _broadcastRoom.Dispose();
        _writer.Dispose();
    }

    private sealed class LightweightBenchmarkRoom : RoomActor
    {
        private ulong _accumulatedTicks;

        protected override void OnTick(TickNumber tick)
        {
            _accumulatedTicks += tick.Value;
        }
    }
}

/// <summary>Entry point for running the Hayabusa BenchmarkDotNet suite.</summary>
public static class Program
{
    /// <summary>Runs all benchmarks in the assembly.</summary>
    public static void Main(string[] args)
    {
        if (args is not null && Array.IndexOf(args, "--quick-compare") >= 0)
        {
            HayabusaHotPathBenchmarks.RunQuickComparison();
            return;
        }

        _ = BenchmarkRunner.Run<HayabusaHotPathBenchmarks>(args: args);
    }
}
