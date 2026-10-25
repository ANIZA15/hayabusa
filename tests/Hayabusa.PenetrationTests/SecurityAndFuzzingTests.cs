using System;
using System.Buffers.Binary;
using System.Text;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Rpc.Security;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Threading;
using Hayabusa.SampleServer;
using Hayabusa.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Hayabusa.PenetrationTests;

/// <summary>
/// Penetration testing and structure-aware fuzzing suite covering all 7 threat vectors defined in the Penetration Testing Skill.
/// </summary>
public sealed class SecurityAndFuzzingTests
{
    // Vector 1: Length-Prefix OOM Attack & Invalid Magic
    [Theory]
    [InlineData(new byte[] { 0x48, 0x59, 0xFF, 0xFF, 0xFF, 0x7F })] // 2GB Length Bomb
    [InlineData(new byte[] { 0x48, 0x59, 0xFF, 0xFF, 0xFF, 0xFF })] // Negative Length (-1)
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })] // Invalid Magic
    public void FrameParser_UnderMaliciousHeader_RejectsImmediatelyWithoutAllocation(byte[] payload)
    {
        // Warmup
        _ = FrameParser.TryParse(payload, maxFrameSize: 64 * 1024, out _);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        FrameParseError error = FrameParser.TryParse(payload, maxFrameSize: 64 * 1024, out _);
        long afterBytes = GC.GetAllocatedBytesForCurrentThread();

        Assert.NotEqual(FrameParseError.None, error);
        Assert.Equal(0L, afterBytes - beforeBytes);
    }

    // Vector 2: MemoryPack Deserialization Collection Length Bomb
    [Fact]
    public void MemoryPackPayloadGuard_WhenCollectionHeaderClaims100MillionElementsInSmallFrame_RejectsWithoutAllocation()
    {
        byte[] maliciousPayload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(maliciousPayload.AsSpan(0, 4), 100_000_000);

        // Warmup
        _ = MemoryPackPayloadGuard.ValidateCollectionHeader(maliciousPayload);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        bool valid = MemoryPackPayloadGuard.ValidateCollectionHeader(maliciousPayload);
        bool deserialized = MemoryPackPayloadGuard.TryDeserializeSafe<int[]>(maliciousPayload, out _, isCollectionOrStringRoot: true);
        long afterBytes = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(valid);
        Assert.False(deserialized);
        Assert.Equal(0L, afterBytes - beforeBytes);
    }

    // Vector 3: Slowloris / Fragmentation Delay DoS Attack
    [Fact]
    public void CoreWorker_UnderSlowlorisOneBytePerSecondDrip_DisconnectsAttackerOnFrameReadTimeout()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TimeProvider: fakeTime), transport, dispatcher);
        var attackerId = new ConnectionId(666UL);

        transport.EnqueueAccept(attackerId);
        // Send only 1 byte of the 12-byte RPC header
        transport.TryEnqueueReceive(attackerId, [0x48]);
        worker.StepIteration();

        // Advance past the 5-second FrameReadTimeout while dripping another byte
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        transport.TryEnqueueReceive(attackerId, [0x59]);
        worker.StepIteration();

        fakeTime.Advance(TimeSpan.FromSeconds(3));
        worker.StepIteration();

        Assert.True(transport.IsDisconnected(attackerId));
        Assert.Equal(0, worker.ActiveConnectionCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    // Vector 4: HTTP Request Smuggling / Header Bomb / CRLF & Null Byte Injection
    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n12345", HttpParseError.RequestSmugglingDetected)]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n12345", HttpParseError.RequestSmugglingDetected)]
    [InlineData("GET / HTTP/1.1\r\nBad Header : val\r\n\r\n", HttpParseError.MalformedHeader)]
    [InlineData("GET / HTTP/1.1\r\nHost: a\rEvil: b\r\n\r\n", HttpParseError.MalformedHeader)]
    public void HttpParser_UnderRequestSmugglingAndHeaderInjection_RejectsDeterministically(
        string rawRequest,
        HttpParseError expectedError)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);

        HttpParseError actual = HttpParser.TryParseRequest(bytes, out _);

        Assert.Equal(expectedError, actual);
    }

    [Fact]
    public void HttpParser_UnderOversizedHeaderAndTooManyHeaders_RejectsBeforeAllocation()
    {
        byte[] oversized = new byte[512];
        oversized.AsSpan().Fill((byte)'A');
        HttpParseError sizeErr = HttpParser.TryParseRequest(oversized, out _, maxHeaderSize: 128, maxHeaderCount: 8);

        string manyHeadersRaw = "GET / HTTP/1.1\r\nH1: 1\r\nH2: 2\r\nH3: 3\r\nH4: 4\r\n\r\n";
        HttpParseError countErr = HttpParser.TryParseRequest(
            Encoding.ASCII.GetBytes(manyHeadersRaw),
            out _,
            maxHeaderSize: 4096,
            maxHeaderCount: 2);

        Assert.Equal(HttpParseError.HeaderTooLarge, sizeErr);
        Assert.Equal(HttpParseError.TooManyHeaders, countErr);
    }

    // Vector 5: Invalid MethodId Out-of-Bounds & HTTP Path Traversal
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void RpcDispatcher_WithInvalidOrBoundaryMethodId_ReturnsFalseWithZeroAllocation(int badMethodId)
    {
        using var responseWriter = new PooledBufferWriter(1024);
        using var requestWriter = new PooledBufferWriter(128);
        var context = new HayabusaContext(CoreIndex.Zero, responseWriter);
        var dispatcher = new RpcDispatcher();
        PooledBufferWriter reqW = requestWriter;
        FrameParser.WriteFrame(ref reqW, new MethodId(badMethodId), RpcFrameFlags.None, ReadOnlySpan<byte>.Empty);
        byte[] frameBytes = requestWriter.WrittenSpan.ToArray();

        // Warmup
        _ = FrameParser.TryParse(frameBytes, 1024, out FrameSlice warm);
        _ = dispatcher.TryDispatchSync(context, in warm, responseWriter);
        responseWriter.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = FrameParser.TryParse(frameBytes, 1024, out FrameSlice slice);
        bool result = dispatcher.TryDispatchSync(context, in slice, responseWriter);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(result);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData("GET /../../etc/passwd HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    [InlineData("GET /%2e%2e/secret HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    [InlineData("GET /..\\win.ini HTTP/1.1\r\nHost: localhost\r\n\r\n")]
    public void HttpParser_UnderPathTraversalAttempt_ReturnsPathTraversalDetected(string rawRequest)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);

        HttpParseError error = HttpParser.TryParseRequest(bytes, out _);

        Assert.Equal(HttpParseError.PathTraversalDetected, error);
    }

    // Vector 6: Packet Flood & Rapid 10,000 Connect-Disconnect DoS
    [Fact]
    public void CoreWorker_UnderTenThousandRapidConnectDisconnectAndFloodCycles_RecoversAllSlabsWithZeroLeak()
    {
        using var pool = new PinnedSlabPool(4096, 64);
        using var transport = new InMemoryTransport(pool);
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero), transport, dispatcher);

        RunTenThousandConnectDisconnectCycles(transport, worker);

        Assert.Equal(0, worker.ActiveConnectionCount);
        Assert.Equal(0, worker.TimingWheel.TrackedCount);
        Assert.Equal(0, worker.RateLimiter.TrackedCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    // Vector 7: Room (Actor) Spoofing & Cross-Tenant Intrusion
    [Fact]
    public void CoreWorker_WhenUnauthenticatedConnectionSendsRpcToRoom_BlocksCrossTenantIntrusion()
    {
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero), transport, dispatcher);
        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));

        var legitimatePlayer = new ConnectionId(1UL);
        var spoofingAttacker = new ConnectionId(999UL);
        room.JoinPlayer(legitimatePlayer);

        using var frameWriter = new PooledBufferWriter(256);
        PooledBufferWriter w = frameWriter;
        BattleRoomServiceClient.WriteMoveAsyncRequest(ref w, new MoveCommand(999f, 999f, 999f));
        var spoofedMsg = new CrossCoreRpcMessage(
            CoreIndex.Zero,
            spoofingAttacker,
            new RoomId(1UL),
            frameWriter.WrittenSpan.ToArray());

        worker.Mailbox.TryEnqueue(in spoofedMsg);
        worker.StepIteration();

        Assert.False(room.TryGetPlayerPosition(spoofingAttacker, out _));
        Assert.Equal(1, room.PlayerCount);
    }

    // Structure-Aware Fuzzing (10,000 deterministic mutations across RPC and HTTP parsers + worker loop)
    [Fact]
    public void StructureAwareFuzzer_TenThousandMutatedPayloads_NeverCrashesAndLeavesZeroOutstandingSlabs()
    {
        var rng = new Random(20261025);
        using var pool = new PinnedSlabPool(8192, 64);
        using var transport = new InMemoryTransport(pool);
        var dispatcher = new RpcDispatcher();
        dispatcher.MapRpc<MoveCommand, WorldSnapshot>(
            1,
            static (ctx, _) => new WorldSnapshot(ctx.CurrentTick, 1));
        dispatcher.MapHttp(HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));

        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, MaxFrameSize: 4096), transport, dispatcher);
        byte[] validRpcTemplate = CreateValidRpcTemplate();
        byte[] validHttpTemplate = "GET /health HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n"u8.ToArray();

        RunFuzzIterations(rng, transport, worker, (validRpcTemplate, validHttpTemplate));

        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(255)]
    public void FrameParser_WithUnsupportedProtocolVersion_RejectsWithZeroAllocation(byte badVersion)
    {
        byte[] frame = [0x48, 0x59, 0x00, 0x00, 0x00, 0x00, badVersion, 0x00, 0x01, 0x00, 0x00, 0x00];
        _ = FrameParser.TryParse(frame, 1024, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        FrameParseError error = FrameParser.TryParse(frame, 1024, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(FrameParseError.UnsupportedProtocolVersion, error);
        Assert.Equal(0L, after - before);
    }

    [Theory]
    [InlineData("GET /test\0evil HTTP/1.1\r\nHost: a\r\n\r\n", HttpParseError.MalformedHeader)]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: -5\r\n\r\n", HttpParseError.PayloadTooLarge)]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 99999999\r\n\r\n", HttpParseError.PayloadTooLarge)]
    [InlineData("GET / HTTP/2.0\r\nHost: a\r\n\r\n", HttpParseError.InvalidRequestLine)]
    public void HttpParser_NullBytesNegativeOrHugeContentLengthAndUnsupportedVersion_RejectsWithZeroAllocation(
        string rawRequest,
        HttpParseError expectedError)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);
        _ = HttpParser.TryParseRequest(bytes, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError actual = HttpParser.TryParseRequest(bytes, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(expectedError, actual);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void CoreWorker_WhenAttackerOverflowsPerConnectionReceiveBuffer_EvictsAttackerAndPreservesLegitimatePlayer()
    {
        using var pool = new PinnedSlabPool(1024, 16);
        using var transport = new InMemoryTransport(pool);
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, MaxFrameSize: 128), transport, dispatcher);
        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));
        dispatcher.RegisterService(room);

        var legitConn = new ConnectionId(10UL);
        var attackerConn = new ConnectionId(99UL);
        room.JoinPlayer(legitConn);
        transport.EnqueueAccept(legitConn);
        transport.EnqueueAccept(attackerConn);

        // MaxFrameSize is 128 -> receive buffer capacity is 256. Send 300 bytes to overflow attacker's buffer.
        byte[] overflowChunk = new byte[300];
        overflowChunk[0] = 0x48;
        overflowChunk[1] = 0x59;
        transport.TryEnqueueReceive(attackerConn, overflowChunk);
        worker.StepIteration();

        Assert.True(transport.IsDisconnected(attackerConn));
        Assert.False(transport.IsDisconnected(legitConn));
        Assert.Equal(1, room.PlayerCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public void HttpRpcTunnel_UnderMaliciousLengthBombBody_Returns400BadRequestWithoutCrashing()
    {
        using var responseWriter = new PooledBufferWriter(1024);
        var context = new HayabusaContext(CoreIndex.Zero, responseWriter);
        var dispatcher = new RpcDispatcher();

        byte[] bombBody = [0x48, 0x59, 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        byte[] header = "POST /rpc HTTP/1.1\r\nHost: localhost\r\nContent-Length: 12\r\n\r\n"u8.ToArray();
        byte[] rawHttp = new byte[header.Length + bombBody.Length];
        header.CopyTo(rawHttp, 0);
        bombBody.CopyTo(rawHttp, header.Length);

        HttpParseError parseErr = HttpParser.TryParseRequest(rawHttp, out HttpRequestView req);
        bool handled = dispatcher.DispatchHttp(context, in req, responseWriter);

        Assert.Equal(HttpParseError.None, parseErr);
        Assert.True(handled);
        Assert.True(responseWriter.WrittenSpan.StartsWith("HTTP/1.1 400 Bad Request"u8));
    }

    private static void RunTenThousandConnectDisconnectCycles(InMemoryTransport transport, CoreWorker worker)
    {
        ReadOnlySpan<byte> badFrame = [0x48, 0x59, 0xFF, 0xFF, 0xFF, 0x7F];
        for (ulong i = 1; i <= 10_000; i++)
        {
            var id = new ConnectionId(i);
            transport.EnqueueAccept(id);
            transport.TryEnqueueReceive(id, badFrame);
            worker.StepIteration();
        }
    }

    private static byte[] CreateValidRpcTemplate()
    {
        using var writer = new PooledBufferWriter(128);
        PooledBufferWriter w = writer;
        var cmd = new MoveCommand(1.0f, 2.0f, 3.0f);
        FrameParser.WriteMemoryPackFrame(ref w, new MethodId(1), RpcFrameFlags.None, in cmd);
        return writer.WrittenSpan.ToArray();
    }

    private static void RunFuzzIterations(
        Random rng,
        InMemoryTransport transport,
        CoreWorker worker,
        (byte[] RpcTemplate, byte[] HttpTemplate) templates)
    {
        byte[] scratch = new byte[128];
        for (int i = 0; i < 10_000; i++)
        {
            byte[] source = (i & 1) == 0 ? templates.RpcTemplate : templates.HttpTemplate;
            int mutatedLen = MutatePayload(rng, source, scratch);
            ReadOnlySpan<byte> fuzzed = scratch.AsSpan(0, mutatedLen);

            _ = FrameParser.TryParse(fuzzed, 4096, out _);
            _ = HttpParser.TryParseRequest(fuzzed, out _);

            var connId = new ConnectionId((ulong)(i + 1));
            transport.TryEnqueueReceive(connId, fuzzed);
            transport.EnqueueDisconnect(connId);
            worker.StepIteration();
        }
    }

    private static int MutatePayload(Random rng, byte[] template, byte[] destination)
    {
        int len = rng.Next(0, Math.Min(template.Length + 16, destination.Length));
        int copyLen = Math.Min(len, template.Length);
        template.AsSpan(0, copyLen).CopyTo(destination);

        if (len > 0)
        {
            int flips = rng.Next(1, 4);
            for (int i = 0; i < flips; i++)
            {
                int pos = rng.Next(0, len);
                destination[pos] ^= (byte)(1 << rng.Next(0, 8));
            }
        }

        return len;
    }
}
