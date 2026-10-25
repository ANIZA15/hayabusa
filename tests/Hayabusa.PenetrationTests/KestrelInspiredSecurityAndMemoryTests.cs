using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Rpc.Security;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Hosting;
using Hayabusa.Runtime.Threading;
using Hayabusa.SampleServer;
using Hayabusa.Transport;
using MemoryPack;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Hayabusa.PenetrationTests;

/// <summary>
/// Kestrel and ASP.NET Core inspired security, vulnerability, and memory/OOM protection tests
/// (covering RFC 9110/9112 strictness, signed Content-Length smuggling, control-char injection,
/// length-prefix integer overflow, MemoryPack UTF-8/UTF-16 string OOM bombs, high-water mark LOH reclamation,
/// mass Slowloris timeout sweeps, and HTTP/Mailbox deserialization fault isolation).
/// </summary>
public sealed class KestrelInspiredSecurityAndMemoryTests
{
    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: +0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: -0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: +5\r\n\r\n12345")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5, 5\r\n\r\n12345")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 0x10\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5 5\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: \r\n\r\n")]
    public void HttpParser_WhenContentLengthHasLeadingSignOrMultipleValues_RejectsSmugglingWithZeroAllocation(string rawRequest)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);
        _ = HttpParser.TryParseRequest(bytes, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(HttpParseError.MalformedHeader, error);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 65537\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 2147483648\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 99999999999999999999\r\n\r\n")]
    public void HttpParser_WhenContentLengthOverflowsInt32OrMaxBodySize_ReturnsPayloadTooLargeWithoutAllocation(string rawRequest)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);
        _ = HttpParser.TryParseRequest(bytes, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(HttpParseError.PayloadTooLarge, error);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData("GET /foo\tbar HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET /foo\u0001bar HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET /foo\u000bbar HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET /foo\u001fbar HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET /foo\u007fbar HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET  /double-space HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET /trailing-space  HTTP/1.1\r\nHost: a\r\n\r\n")]
    public void HttpParser_WhenRequestTargetContainsControlCharsOrExtraSpaces_ReturnsInvalidRequestLine(string rawRequest)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(rawRequest);
        _ = HttpParser.TryParseRequest(bytes, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(HttpParseError.InvalidRequestLine, error);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData("GET /index.html%00.png HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    [InlineData("GET /..%5csecret HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    [InlineData("GET /a%5Cb HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    public void HttpParser_WhenRequestTargetContainsPercentEncodedNullOrBackslash_ReturnsPathTraversalDetected(string rawRequest)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);

        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);

        Assert.Equal(HttpParseError.PathTraversalDetected, error);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Inject: val\u0001ue\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Inject: val\u000bue\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Inject: val\u000cue\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Inject: val\u001bue\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Inject: val\u007fue\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n obs-fold: 1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n\tobs-fold: 1\r\n\r\n")]
    public void HttpParser_WhenHeaderValueContainsControlCharsOrObsFold_ReturnsMalformedHeader(string rawRequest)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);
        _ = HttpParser.TryParseRequest(bytes, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(HttpParseError.MalformedHeader, error);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue - 4, int.MaxValue)]
    [InlineData(16, -1)]
    public void FrameParser_WhenMaxFrameSizeAndPayloadLengthAreNearIntMaxValue_PreventsIntegerOverflowWithoutThrowing(
        int declaredPayloadLength,
        int maxFrameSize)
    {
        Span<byte> frame = stackalloc byte[RpcHeader.Size];
        var header = new RpcHeader(declaredPayloadLength, RpcHeader.CurrentVersion, RpcFrameFlags.None, new MethodId(1));
        header.WriteTo(frame);

        FrameParseError error = FrameParser.TryParse(frame, maxFrameSize, out _);

        Assert.Equal(FrameParseError.PayloadTooLarge, error);
    }

    [Fact]
    public void MemoryPackPayloadGuard_WhenUtf8StringHeaderClaims100MillionUtf16CharsIn12ByteFrame_RejectsStringOomBombWithZeroAllocation()
    {
        // Craft a 12-byte MemoryPack UTF-8 string bomb:
        // Bytes 0..3: ~4 (-5) = 4 UTF-8 bytes (fits in buffer!)
        // Bytes 4..7: 100,000,000 UTF-16 chars (would allocate 200 MB string if unchecked!)
        // Bytes 8..11: "AAAA"
        byte[] utf8StringBomb = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(utf8StringBomb.AsSpan(0, 4), ~4);
        BinaryPrimitives.WriteInt32LittleEndian(utf8StringBomb.AsSpan(4, 4), 100_000_000);
        "AAAA"u8.CopyTo(utf8StringBomb.AsSpan(8, 4));

        byte[] validUtf8String = MemoryPackSerializer.Serialize("hayabusa");

        // Warmup
        _ = MemoryPackPayloadGuard.TryDeserializeSafe<string>(utf8StringBomb, out _);
        _ = MemoryPackPayloadGuard.TryDeserializeSafe<string>(validUtf8String, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool validHeader = MemoryPackPayloadGuard.ValidateCollectionHeader(utf8StringBomb);
        bool deserializedBomb = MemoryPackPayloadGuard.TryDeserializeSafe<string>(utf8StringBomb, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(validHeader);
        Assert.False(deserializedBomb);
        Assert.Equal(0L, after - before);

        Assert.True(MemoryPackPayloadGuard.TryDeserializeSafe(validUtf8String, out string? safeStr));
        Assert.Equal("hayabusa", safeStr);
    }

    [Fact]
    public void CoreWorker_WhenClientSendsUtf8StringUtf16LengthBombToRpcEndpoint_EvictsAttackerWithZeroStringAllocation()
    {
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapRpc<string, int>(1, static s => s.Length);
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero), transport, dispatcher);

        byte[] utf8StringBomb = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(utf8StringBomb.AsSpan(0, 4), ~4);
        BinaryPrimitives.WriteInt32LittleEndian(utf8StringBomb.AsSpan(4, 4), 100_000_000);
        "AAAA"u8.CopyTo(utf8StringBomb.AsSpan(8, 4));

        using var frameWriter = new PooledBufferWriter(64);
        PooledBufferWriter w = frameWriter;
        FrameParser.WriteFrame(ref w, new MethodId(1), RpcFrameFlags.None, utf8StringBomb);

        var attacker = new ConnectionId(5501UL);
        transport.EnqueueAccept(attacker);
        transport.TryEnqueueReceive(attacker, frameWriter.WrittenSpan);
        worker.StepIteration();

        Assert.True(transport.IsDisconnected(attacker));
        Assert.Equal(0, worker.ActiveConnectionCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public async Task CoreWorkerAndHybridBridge_WhenCorruptedMemoryPackArrivesViaHttpTunnelOrMailbox_IsolatesFaultWithoutCrash()
    {
        using HayabusaApp app = HayabusaApp.CreateBuilder(["--transport", "inmemory", "--workers", "1"]).Build();
        app.MapRpc<string, int>(1, static s => s.Length);
        BattleRoomService room = app.RegisterRoom<BattleRoomService>(new RoomId(1UL));

        // Build an RPC frame with a corrupted MemoryPack string payload
        using var badRpcFrame = new PooledBufferWriter(64);
        PooledBufferWriter fw = badRpcFrame;
        FrameParser.WriteFrame(ref fw, new MethodId(1), RpcFrameFlags.None, [0xF4, 0x01, 0x00, 0x00]);
        byte[] corruptedFrameBytes = badRpcFrame.WrittenSpan.ToArray();

        // 1. Test POST /rpc HTTP tunnel with corrupted MemoryPack body -> returns 400 Bad Request
        byte[] httpHeader = Encoding.ASCII.GetBytes(
            $"POST /rpc HTTP/1.1\r\nHost: localhost\r\nContent-Length: {corruptedFrameBytes.Length}\r\n\r\n");
        byte[] fullHttp = new byte[httpHeader.Length + corruptedFrameBytes.Length];
        httpHeader.CopyTo(fullHttp, 0);
        corruptedFrameBytes.CopyTo(fullHttp, httpHeader.Length);

        using var httpOut = new PooledBufferWriter(512);
        bool httpHandled = HayabusaHybridBridge.DispatchRawHttpRequest(app, fullHttp, httpOut);
        Assert.True(httpHandled);
        Assert.StartsWith("HTTP/1.1 400 Bad Request", Encoding.UTF8.GetString(httpOut.WrittenSpan), StringComparison.Ordinal);

        // 2. Test HayabusaHybridBridge.DispatchRawRpcFrameAsync with corrupted MemoryPack body -> returns false cleanly
        httpOut.Clear();
        bool rpcHandled = await HayabusaHybridBridge.DispatchRawRpcFrameAsync(app, corruptedFrameBytes, httpOut);
        Assert.False(rpcHandled);

        // 3. Test Cross-Core Mailbox with authorized room player sending truncated MoveCommand payload -> evicts player safely
        var badRoomPlayer = new ConnectionId(7701UL);
        room.JoinPlayer(badRoomPlayer);
        using var truncatedMoveFrame = new PooledBufferWriter(64);
        PooledBufferWriter mw = truncatedMoveFrame;
        FrameParser.WriteFrame(ref mw, new MethodId(1), RpcFrameFlags.None, [0x01, 0x02]);
        app.Workers[0].Mailbox.TryEnqueue(new CrossCoreRpcMessage(
            CoreIndex.Zero,
            badRoomPlayer,
            new RoomId(1UL),
            truncatedMoveFrame.WrittenSpan.ToArray()));
        app.Workers[0].StepIteration();

        Assert.Equal(0, room.PlayerCount);
    }

    [Fact]
    public void PooledBufferWriter_WhenBurstExceedsMaxRetainedCapacity_ShrinksBackToInitialCapacityOnClearToPreventLohRetention()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 4096, maxRetainedCapacity: 16 * 1024);
        Assert.Equal(4096, writer.Capacity);
        Assert.Equal(16 * 1024, writer.MaxRetainedCapacity);

        // Normal write within MaxRetainedCapacity (8 KB) retains the grown buffer across Clear()
        writer.Write(new byte[8192]);
        int retainedCapacity = writer.Capacity;
        writer.Clear();
        Assert.Equal(retainedCapacity, writer.Capacity);

        // Burst write exceeding MaxRetainedCapacity (128 KB > 16 KB) grows buffer, then shrinks back to 4096 on Clear()
        writer.Write(new byte[128 * 1024]);
        Assert.True(writer.Capacity >= 128 * 1024);

        writer.Clear();

        Assert.Equal(0, writer.WrittenCount);
        Assert.Equal(4096, writer.Capacity);
    }

    [Fact]
    public void CoreWorker_WhenTwoHundredSlowlorisConnectionsExpireInSingleTick_SweepsAndEvictsAllBeyondScratchBatchSize()
    {
        var fakeTime = new FakeTimeProvider();
        using var pool = new PinnedSlabPool(1024, 256);
        using var transport = new InMemoryTransport(pool);
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TimeProvider: fakeTime), transport, dispatcher);

        EnqueueTwoHundredSlowlorisConnections(transport, worker);
        Assert.Equal(200, worker.ActiveConnectionCount);
        Assert.Equal(200, worker.TimingWheel.TrackedCount);

        // Advance past the 5s FrameReadTimeout: single StepIteration must loop and evict all 200 (> 64 scratch size)
        fakeTime.Advance(TimeSpan.FromSeconds(6));
        worker.StepIteration();

        Assert.Equal(0, worker.ActiveConnectionCount);
        Assert.Equal(0, worker.TimingWheel.TrackedCount);
        Assert.Equal(0, pool.OutstandingRentals);
        Assert.True(transport.IsDisconnected(new ConnectionId(1UL)));
        Assert.True(transport.IsDisconnected(new ConnectionId(200UL)));
    }

    [Fact]
    public void CoreWorker_WhenPipelinedValidRpcFollowedByMalformedRpcArrives_DisconnectsWithoutTrailingResponseAfterEvict()
    {
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapRpc<int, int>(1, static x => x + 1);
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero), transport, dispatcher);
        var conn = new ConnectionId(9100UL);

        using var writer = new PooledBufferWriter(128);
        PooledBufferWriter w = writer;
        int validInput = 10;
        FrameParser.WriteMemoryPackFrame(ref w, new MethodId(1), RpcFrameFlags.None, in validInput);
        // Append a 2GB Length-Bomb header right after the valid frame in the same packet
        writer.Write([0x48, 0x59, 0xFF, 0xFF, 0xFF, 0x7F]);

        transport.EnqueueAccept(conn);
        transport.TryEnqueueReceive(conn, writer.WrittenSpan);
        worker.StepIteration();

        Assert.True(transport.IsDisconnected(conn));
        Assert.Empty(transport.GetSentPayloads(conn));
        Assert.Equal(0, worker.ActiveConnectionCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    private static void EnqueueTwoHundredSlowlorisConnections(InMemoryTransport transport, CoreWorker worker)
    {
        for (ulong i = 1; i <= 200; i++)
        {
            var id = new ConnectionId(i);
            transport.EnqueueAccept(id);
            transport.TryEnqueueReceive(id, [0x48]);
        }

        // 200 Accepts + 200 Receives = 400 CQEs (> 256 CQE batch size), so two iterations drain all 400
        worker.StepIteration();
        worker.StepIteration();
    }
}
