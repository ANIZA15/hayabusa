using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Client;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Rpc.Security;
using Hayabusa.SampleServer;
using MemoryPack;
using Xunit;

namespace Hayabusa.Tests;

public sealed class RpcHttpAndClientExtendedTests
{
    private static readonly int[] SampleTwoInts = [10, 20];
    [Fact]
    public void RpcAndHttpAttributes_PropertiesAndValidation_StoreExpectedMetadata()
    {
        var svcAttr = new HayabusaServiceAttribute { Prefix = "/v1" };
        var rpcDefault = new RpcAttribute();
        var rpcExplicit = new RpcAttribute(42);
        var getAttr = new GetAttribute("/status");
        var postAttr = new PostAttribute("/submit");

        Assert.Equal("/v1", svcAttr.Prefix);
        Assert.Equal(0, rpcDefault.MethodId);
        Assert.Equal(42, rpcExplicit.MethodId);
        Assert.Equal("/status", getAttr.Template);
        Assert.Equal("/submit", postAttr.Template);
        Assert.Throws<ArgumentNullException>(() => new GetAttribute(null!));
        Assert.Throws<ArgumentNullException>(() => new PostAttribute(null!));
    }

    [Fact]
    public void RpcHeader_WriteToShortSpanAndTotalFrameLength_EnforceConstraints()
    {
        var header = new RpcHeader(20, RpcHeader.CurrentVersion, RpcFrameFlags.Response, new MethodId(7));
        Span<byte> shortSpan = stackalloc byte[8];

        Assert.Equal(32, header.TotalFrameLength);
        try
        {
            header.WriteTo(shortSpan);
            Assert.Fail("Expected ArgumentException for short span.");
        }
        catch (ArgumentException)
        {
            // Expected
        }
    }

    [Fact]
    public void FrameParser_PartialHeadersAndUnsupportedVersion_ReturnExpectedErrors()
    {
        Span<byte> buffer = stackalloc byte[16];
        Assert.Equal(FrameParseError.IncompleteData, FrameParser.TryParse(ReadOnlySpan<byte>.Empty, 1024, out _));
        Assert.Equal(FrameParseError.IncompleteData, FrameParser.TryParse([0x48], 1024, out _));
        Assert.Equal(FrameParseError.IncompleteData, FrameParser.TryParse([0x48, 0x59, 0x01], 1024, out _));

        // 8 bytes with valid magic and payload length 4, but header is still incomplete (< 12 bytes)
        byte[] eightBytes = [0x48, 0x59, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00];
        Assert.Equal(FrameParseError.IncompleteData, FrameParser.TryParse(eightBytes, 1024, out _));

        // Complete 12-byte header with unsupported version = 99
        var badVersionHeader = new RpcHeader(0, Version: 99, RpcFrameFlags.None, new MethodId(1));
        badVersionHeader.WriteTo(buffer);
        Assert.Equal(FrameParseError.UnsupportedProtocolVersion, FrameParser.TryParse(buffer.Slice(0, 12), 1024, out _));
    }

    [Fact]
    public void FrameParser_GenericBufferWriterOverload_SerializesViaThreadLocalScratchAndGrowsWhenLarge()
    {
        var genericWriter = new ArrayBufferWriter<byte>(256);
        var cmd = new MoveCommand(10f, 20f, 30f);

        FrameParser.WriteMemoryPackFrame(ref genericWriter, new MethodId(3), RpcFrameFlags.None, in cmd);
        FrameParseError err = FrameParser.TryParse(genericWriter.WrittenSpan, 1024, out FrameSlice slice);
        MoveCommand roundtripped = MemoryPackSerializer.Deserialize<MoveCommand>(slice.Payload);

        Assert.Equal(FrameParseError.None, err);
        Assert.Equal(cmd, roundtripped);

        // Also exercise scratch buffer growth above 64KB
        byte[] largeArray = new byte[70_000];
        largeArray[0] = 0x7E;
        genericWriter.Clear();
        FrameParser.WriteMemoryPackFrame(ref genericWriter, new MethodId(4), RpcFrameFlags.None, in largeArray);
        FrameParseError largeErr = FrameParser.TryParse(genericWriter.WrittenSpan, 128 * 1024, out FrameSlice largeSlice);
        Assert.Equal(FrameParseError.None, largeErr);
        Assert.True(largeSlice.Payload.Length > 70_000);
    }

    [Theory]
    [InlineData("GET /a HTTP/1.1\r\n\r\n", HttpMethodKind.Get)]
    [InlineData("POST /a HTTP/1.1\r\nContent-Length: 4\r\n\r\ntest", HttpMethodKind.Post)]
    [InlineData("PUT /a HTTP/1.0\r\n\r\n", HttpMethodKind.Put)]
    [InlineData("DELETE /a HTTP/1.1\r\n\r\n", HttpMethodKind.Delete)]
    [InlineData("HEAD /a HTTP/1.1\r\n\r\n", HttpMethodKind.Head)]
    [InlineData("OPTIONS /a HTTP/1.1\r\n\r\n", HttpMethodKind.Options)]
    public void HttpParser_SupportedMethodsAndVersions_ParseSuccessfully(string rawRequest, HttpMethodKind expectedMethod)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);

        HttpParseError err = HttpParser.TryParseRequest(bytes, out HttpRequestView view);

        Assert.Equal(HttpParseError.None, err);
        Assert.Equal(expectedMethod, view.Method);
        Assert.Equal("/a", Encoding.UTF8.GetString(view.Path));
        Assert.Equal(bytes.Length, view.ConsumedBytes);
    }

    [Theory]
    [InlineData("PATCH /a HTTP/1.1\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("BAD /a HTTP/1.1\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("FOUR /a HTTP/1.1\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET /a HTTP/2.0\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET /a HTTP/1\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET noslash HTTP/1.1\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET /onlyonespace\r\n\r\n", HttpParseError.InvalidRequestLine)]
    [InlineData("GET / HTTP/1.1\r\nNoColonHeader\r\n\r\n", HttpParseError.MalformedHeader)]
    [InlineData("GET / HTTP/1.1\r\n:EmptyHeaderName\r\n\r\n", HttpParseError.MalformedHeader)]
    [InlineData("GET / HTTP/1.1\r\nContent-Length: abc\r\n\r\n", HttpParseError.MalformedHeader)]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 100000\r\n\r\n", HttpParseError.PayloadTooLarge)]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\n123", HttpParseError.IncompleteData)]
    [InlineData("GET / HTTP/1.1\r\nHost: local", HttpParseError.IncompleteData)]
    public void HttpParser_EdgeCasesAndInvalidInputs_ReturnExpectedParseError(string rawRequest, HttpParseError expectedError)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(rawRequest);

        HttpParseError actual = HttpParser.TryParseRequest(bytes, out _);

        Assert.Equal(expectedError, actual);
    }

    [Fact]
    public void HttpParser_NullByteInCompleteOrIncompleteHeader_ReturnsMalformedHeader()
    {
        byte[] incompleteWithNull = "GET / HTTP/1.1\r\nHost: a\0b"u8.ToArray();
        byte[] completeWithNull = "GET / HTTP/1.1\r\nHost: a\0b\r\n\r\n"u8.ToArray();

        Assert.Equal(HttpParseError.MalformedHeader, HttpParser.TryParseRequest(incompleteWithNull, out _));
        Assert.Equal(HttpParseError.MalformedHeader, HttpParser.TryParseRequest(completeWithNull, out _));
    }

    [Fact]
    public void HttpResponseWriter_AllContentLengthDigitsAndHelperMethods_FormatValidHttpResponses()
    {
        using var writer = new PooledBufferWriter(1024);
        PooledBufferWriter w = writer;

        // 1-digit Content-Length (2 bytes)
        HttpResponseWriter.WriteOkUtf8(ref w, "OK"u8);
        Assert.Contains("Content-Length: 2\r\n\r\nOK", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        // 2-digit Content-Length (15 bytes)
        writer.Clear();
        HttpResponseWriter.WriteOkJsonUtf8(ref w, "{\"status\":\"ok\"}"u8);
        string jsonRes = Encoding.UTF8.GetString(writer.WrittenSpan);
        Assert.Contains("application/json", jsonRes, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 15\r\n\r\n{\"status\":\"ok\"}", jsonRes, StringComparison.Ordinal);

        // 3-digit Content-Length (120 bytes)
        writer.Clear();
        byte[] body120 = new byte[120];
        body120.AsSpan().Fill((byte)'x');
        HttpResponseWriter.WriteOkUtf8(ref w, body120);
        Assert.Contains("Content-Length: 120\r\n\r\n", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        // Formatted & Concat & 400 & 404
        writer.Clear();
        HttpResponseWriter.WriteFormattedOk(ref w, "Count={0}", 42);
        Assert.Contains("Count=42", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        writer.Clear();
        HttpResponseWriter.WriteConcatOk(ref w, "Tick:", 99);
        Assert.Contains("Tick:99", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        writer.Clear();
        HttpResponseWriter.WriteBadRequest(ref w);
        Assert.StartsWith("HTTP/1.1 400 Bad Request", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        writer.Clear();
        HttpResponseWriter.WriteNotFound(ref w);
        Assert.StartsWith("HTTP/1.1 404 Not Found", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);
    }

    [Fact]
    public void HayabusaContext_BindAndWriteHelpers_WriteDirectlyToResponseBuffer()
    {
        using var writer = new PooledBufferWriter(512);
        var ctx = new HayabusaContext(new CoreIndex(2), writer);

        ctx.Bind(new ConnectionId(10UL), new RoomId(20UL), new TickNumber(30UL));
        ctx.WriteZString("Player:{0}", 10);

        Assert.Equal(new CoreIndex(2), ctx.CoreIndex);
        Assert.Equal(new ConnectionId(10UL), ctx.ConnectionId);
        Assert.Equal(new RoomId(20UL), ctx.RoomId);
        Assert.Equal(new TickNumber(30UL), ctx.CurrentTick);
        Assert.Same(writer, ctx.ResponseWriter);
        Assert.Contains("Player:10", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RpcDispatcher_JumpTableFallbackAndMultipleServices_DispatchCorrectly()
    {
        using var writer = new PooledBufferWriter(1024);
        var ctx = new HayabusaContext(CoreIndex.Zero, writer);
        var dispatcher = new RpcDispatcher();

        // Register slow async service first (returns false for unrecognized method), then a second service
        dispatcher.RegisterService(new DelayedPassThroughService());
        using var battleRoom = new BattleRoomService();
        dispatcher.RegisterService(battleRoom);

        // Register minimal API handlers: parameterless (id=50) and high MethodId outside 1024 jump table (id=2048)
        dispatcher.MapRpc(50, static () => 777);
        dispatcher.MapRpc<int, int>(2048, static x => x * 2);

        // 1. Test high MethodId (2048) via TryDispatchSync
        using var reqWriter = new PooledBufferWriter(128);
        PooledBufferWriter rw = reqWriter;
        int inputVal = 21;
        FrameParser.WriteMemoryPackFrame(ref rw, new MethodId(2048), RpcFrameFlags.None, in inputVal);
        _ = FrameParser.TryParse(reqWriter.WrittenSpan, 1024, out FrameSlice highSlice);
        Assert.True(dispatcher.TryDispatchSync(ctx, in highSlice, writer));

        // 2. Test async service chain fallthrough to BattleRoomService (MethodId = 1) and minimal handler (MethodId = 50)
        writer.Clear();
        bool handledByMinimalAfterAsync = await dispatcher.DispatchAsync(ctx, new MethodId(50), ReadOnlyMemory<byte>.Empty, writer);
        Assert.True(handledByMinimalAfterAsync);

        writer.Clear();
        byte[] movePayload = MemoryPackSerializer.Serialize(new MoveCommand(1f, 2f, 3f));
        bool handledBySecondService = await dispatcher.DispatchAsync(ctx, new MethodId(1), movePayload, writer);
        Assert.True(handledBySecondService);

        // 3. Test unknown MethodId returns false and writes Error frame
        writer.Clear();
        bool unknownResult = await dispatcher.DispatchAsync(ctx, new MethodId(9999), ReadOnlyMemory<byte>.Empty, writer);
        _ = FrameParser.TryParse(writer.WrittenSpan, 1024, out FrameSlice errSlice);
        Assert.False(unknownResult);
        Assert.Equal(RpcFrameFlags.Error, errSlice.Header.Flags);
    }

    [Fact]
    public void RpcDispatcher_DispatchHttp_RoutesToServiceOrMinimalOr404()
    {
        using var writer = new PooledBufferWriter(1024);
        var ctx = new HayabusaContext(CoreIndex.Zero, writer);
        var dispatcher = new RpcDispatcher();
        using var battleRoom = new BattleRoomService();
        dispatcher.RegisterService(battleRoom);
        dispatcher.MapHttp(HttpMethodKind.Post, "/custom", static c => c.WriteUtf8("CUSTOM"u8));

        _ = HttpParser.TryParseRequest("GET /room/status HTTP/1.1\r\n\r\n"u8, out HttpRequestView svcReq);
        Assert.True(dispatcher.DispatchHttp(ctx, in svcReq, writer));

        writer.Clear();
        _ = HttpParser.TryParseRequest("POST /custom HTTP/1.1\r\n\r\n"u8, out HttpRequestView minReq);
        Assert.True(dispatcher.DispatchHttp(ctx, in minReq, writer));
        Assert.Contains("CUSTOM", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);

        writer.Clear();
        _ = HttpParser.TryParseRequest("GET /nonexistent HTTP/1.1\r\n\r\n"u8, out HttpRequestView notFoundReq);
        Assert.False(dispatcher.DispatchHttp(ctx, in notFoundReq, writer));
        Assert.Contains("404 Not Found", Encoding.UTF8.GetString(writer.WrittenSpan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HayabusaRpcClient_InMemoryOverloadsAndErrorHandling_WorkDeterministically()
    {
        var dispatcher = new RpcDispatcher();
        dispatcher.MapRpc<int, int>(1, static x => x + 10);
        dispatcher.MapRpc(2, static () => 99);

        using var client = HayabusaRpcClient.CreateInMemory(dispatcher);

        Assert.Equal(15, await client.CallAsync<int, int>(1, 5));
        Assert.Equal(99, await client.CallAsync<int>(2));
        await client.CallVoidAsync(new MethodId(1), 5);
        await client.CallVoidAsync(new MethodId(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.CallAsync<int>(999));

        client.Dispose();
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.CallAsync<int>(2));
    }

    [Fact]
    public async Task HayabusaRpcClient_OverTcpLoopback_SkipsInterleavedBroadcastsAndHandlesResponses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = RunSimulatedTcpRpcServerAsync(listener);
        using HayabusaRpcClient client = await HayabusaRpcClient.ConnectAsync("127.0.0.1", port);

        int echoResult = await client.CallAsync<int, int>(1, 42);
        await client.CallVoidAsync(new MethodId(2));

        Assert.Equal(84, echoResult);
        await serverTask;
    }

    [Fact]
    public void MemoryPackPayloadGuard_EdgeCasesAndCorruptedPayloads_HandledSafely()
    {
        // Under 4 bytes -> false
        Assert.False(MemoryPackPayloadGuard.ValidateCollectionHeader([1, 2, 3]));

        // Null collection (-1) -> true
        byte[] nullMarker = [0xFF, 0xFF, 0xFF, 0xFF];
        Assert.True(MemoryPackPayloadGuard.ValidateCollectionHeader(nullMarker));

        // Negative other than -1 -> false
        byte[] minusTwo = [0xFE, 0xFF, 0xFF, 0xFF];
        Assert.False(MemoryPackPayloadGuard.ValidateCollectionHeader(minusTwo));

        // Valid 2-element int array
        byte[] validArray = MemoryPackSerializer.Serialize(SampleTwoInts);
        Assert.True(MemoryPackPayloadGuard.ValidateCollectionHeader(validArray, minElementByteSize: 4));
        Assert.True(MemoryPackPayloadGuard.TryDeserializeSafe(validArray, out int[]? parsed, isCollectionOrStringRoot: true));
        Assert.NotNull(parsed);
        Assert.Equal(SampleTwoInts, parsed);

        // Corrupted payload for a complex type that triggers MemoryPackSerializationException
        byte[] corruptedString = [0x01, 0x00, 0x00, 0x00];
        Assert.False(MemoryPackPayloadGuard.TryDeserializeSafe<string>(corruptedString, out _));
    }

    private static async Task RunSimulatedTcpRpcServerAsync(TcpListener listener)
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = peer.GetStream();
        byte[] readBuf = new byte[256];

        // 1st request: client calls MethodId 1 with int=42; server sends a Broadcast frame first, then the Response frame
        int n1 = await stream.ReadAsync(readBuf);
        Assert.True(n1 >= RpcHeader.Size);

        using var outWriter = new PooledBufferWriter(256);
        PooledBufferWriter w = outWriter;
        int broadcastDummy = 999;
        int responseValue = 84;
        FrameParser.WriteMemoryPackFrame(ref w, new MethodId(100), RpcFrameFlags.Broadcast, in broadcastDummy);
        FrameParser.WriteMemoryPackFrame(ref w, new MethodId(1), RpcFrameFlags.Response, in responseValue);
        await stream.WriteAsync(outWriter.WrittenMemory);

        // 2nd request: client calls CallVoidAsync on MethodId 2; server sends a Broadcast frame then empty Response ack
        int n2 = await stream.ReadAsync(readBuf);
        Assert.True(n2 >= RpcHeader.Size);
        outWriter.Clear();
        FrameParser.WriteFrame(ref w, new MethodId(100), RpcFrameFlags.Broadcast, [1, 2]);
        FrameParser.WriteFrame(ref w, new MethodId(2), RpcFrameFlags.Response, ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(outWriter.WrittenMemory);
    }

    private sealed class DelayedPassThroughService : IHayabusaRpcService
    {
        public async ValueTask<bool> TryDispatchRpcAsync(
            HayabusaContext context,
            MethodId methodId,
            ReadOnlyMemory<byte> payload,
            IBufferWriter<byte> responseWriter)
        {
            await Task.Yield();
            return false;
        }

        public bool TryDispatchHttp(
            HayabusaContext context,
            in HttpRequestView request,
            IBufferWriter<byte> responseWriter) => false;
    }
}
