using System;
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

/// <summary>
/// BenchmarkDotNet suite covering all 4 mandatory Hayabusa hot-path targets:
/// 1. Frame Parse + HTTP/RPC Routing (0 B allocation)
/// 2. MemoryPack Unary RPC Struct Roundtrip (0 B allocation)
/// 3. Room Broadcast (1 Room x 100 Players x 60fps Zero-Copy Shared Reference, 0 B allocation)
/// 4. LogicLooper Tick Scheduler across 1,000 Rooms (0 B allocation)
/// </summary>
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public sealed class HayabusaHotPathBenchmarks : IDisposable
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

        var seedWriter = new PooledBufferWriter(256);
        var move = new MoveCommand(10.5f, 20.0f, -3.25f);
        FrameParser.WriteMemoryPackFrame(ref seedWriter, new MethodId(1), RpcFrameFlags.None, in move);
        _sampleRpcRequestFrame = seedWriter.WrittenSpan.ToArray();
        seedWriter.Dispose();

        _sampleHttpRequestBytes = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();

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

    /// <summary>Benchmark 1: Binary Frame Parse + HTTP Request Parse &amp; Route Dispatch (0 B target).</summary>
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

    /// <summary>Benchmark 2: MemoryPack Unary RPC Frame Parse + Deserialize + Handler + Serialize (0 B target).</summary>
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

    /// <summary>Benchmark 3: 1 Room x 100 Players Broadcast using 1 serialization + zero-copy shared slice (0 B target).</summary>
    [Benchmark]
    public int RoomBroadcast100Players()
    {
        _submissionQueue.Clear();
        var snapshot = new WorldSnapshot(new TickNumber(42UL), 100);
        return _broadcastRoom.BroadcastSnapshot(new MethodId(100), in snapshot);
    }

    /// <summary>Benchmark 4: Tick 1,000 Rooms on a single core worker (0 B target).</summary>
    [Benchmark]
    public int Tick1000Rooms()
    {
        _thousandRoomManager.TickAllRooms(new TickNumber(1UL));
        return _thousandRoomManager.ActiveCount;
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
        _ = BenchmarkRunner.Run<HayabusaHotPathBenchmarks>( args: args);
    }
}
