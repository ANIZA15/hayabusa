using System;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.SampleServer;
using Xunit;

namespace Hayabusa.Tests;

public sealed class FrameAndHttpParserTests
{
    [Fact]
    public void TryParse_SingleCompleteFrame_ParsesHeaderAndPayload()
    {
        using var writer = new PooledBufferWriter(256);
        byte[] expectedPayload = [10, 20, 30, 40];
        PooledBufferWriter w = writer;
        FrameParser.WriteFrame(ref w, new MethodId(5), RpcFrameFlags.None, expectedPayload);

        FrameParseError error = FrameParser.TryParse(writer.WrittenSpan, 1024, out FrameSlice slice);

        Assert.Equal(FrameParseError.None, error);
        Assert.Equal(new MethodId(5), slice.Header.MethodId);
        Assert.Equal(4, slice.Header.PayloadLength);
        Assert.True(slice.Payload.SequenceEqual(expectedPayload));
    }

    [Fact]
    public void TryParse_WhenBufferIsFragmentedOneByteAtATime_CompletesOnlyOnFinalByte()
    {
        using var writer = new PooledBufferWriter(256);
        byte[] payload = [1, 2, 3, 4, 5, 6];
        PooledBufferWriter w = writer;
        FrameParser.WriteFrame(ref w, new MethodId(9), RpcFrameFlags.None, payload);
        ReadOnlySpan<byte> fullFrame = writer.WrittenSpan;
        var receiveBuffer = new ContiguousReceiveBuffer(256);

        FeedAllExceptLastByte(receiveBuffer, fullFrame);
        FrameParseError beforeLast = FrameParser.TryParse(receiveBuffer.ReadableSpan, 1024, out _);
        receiveBuffer.TryAppend(fullFrame.Slice(fullFrame.Length - 1, 1));
        FrameParseError onLast = FrameParser.TryParse(receiveBuffer.ReadableSpan, 1024, out FrameSlice finalSlice);

        Assert.Equal(FrameParseError.IncompleteData, beforeLast);
        Assert.Equal(FrameParseError.None, onLast);
        Assert.Equal(fullFrame.Length, finalSlice.ConsumedBytes);
    }

    [Fact]
    public void TryParse_ThreePipelinedFramesInSingleBuffer_ProcessesAllThreeSequentially()
    {
        using var writer = new PooledBufferWriter(512);
        PooledBufferWriter w = writer;
        FrameParser.WriteFrame(ref w, new MethodId(1), RpcFrameFlags.None, [0xAA]);
        FrameParser.WriteFrame(ref w, new MethodId(2), RpcFrameFlags.None, [0xBB, 0xCC]);
        FrameParser.WriteFrame(ref w, new MethodId(3), RpcFrameFlags.None, [0xDD, 0xEE, 0xFF]);

        var receiveBuffer = new ContiguousReceiveBuffer(512);
        receiveBuffer.TryAppend(writer.WrittenSpan);

        int framesParsed = DrainAllFramesCount(receiveBuffer);

        Assert.Equal(3, framesParsed);
        Assert.Equal(0, receiveBuffer.ReadableBytes);
    }

    [Theory]
    [InlineData(0, 64, FrameParseError.None)]
    [InlineData(64, 64, FrameParseError.None)]
    [InlineData(65, 64, FrameParseError.PayloadTooLarge)]
    public void TryParse_BoundaryPayloadSizes_EnforcesMaxFrameSize(
        int payloadSize,
        int maxFrameSize,
        FrameParseError expected)
    {
        using var writer = new PooledBufferWriter(256);
        byte[] payload = new byte[payloadSize];
        PooledBufferWriter w = writer;
        FrameParser.WriteFrame(ref w, new MethodId(1), RpcFrameFlags.None, payload);

        FrameParseError actual = FrameParser.TryParse(writer.WrittenSpan, maxFrameSize, out _);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DispatchRpc_WarmHotPath_AllocatesZeroBytes()
    {
        using var responseWriter = new PooledBufferWriter(4096);
        using var requestWriter = new PooledBufferWriter(256);
        var context = new HayabusaContext(CoreIndex.Zero, responseWriter);
        var dispatcher = new RpcDispatcher();
        dispatcher.MapRpc<MoveCommand, WorldSnapshot>(
            1,
            static (ctx, _) => new WorldSnapshot(ctx.CurrentTick, 1));

        var cmd = new MoveCommand(1f, 2f, 3f);
        PooledBufferWriter reqW = requestWriter;
        FrameParser.WriteMemoryPackFrame(ref reqW, new MethodId(1), RpcFrameFlags.None, in cmd);
        byte[] frameBytes = requestWriter.WrittenSpan.ToArray();

        // Warmup
        _ = FrameParser.TryParse(frameBytes, 4096, out FrameSlice warmupSlice);
        _ = dispatcher.TryDispatchSync(context, in warmupSlice, responseWriter);
        responseWriter.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = FrameParser.TryParse(frameBytes, 4096, out FrameSlice slice);
        bool dispatched = dispatcher.TryDispatchSync(context, in slice, responseWriter);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(dispatched);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void HttpParser_ValidGetRequest_ParsesAndWritesOkWithZeroAllocation()
    {
        using var writer = new PooledBufferWriter(1024);
        byte[] raw = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
        PooledBufferWriter w = writer;

        // Warmup
        _ = HttpParser.TryParseRequest(raw, out _);
        HttpResponseWriter.WriteOkUtf8(ref w, "OK"u8);
        writer.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpParseError err = HttpParser.TryParseRequest(raw, out HttpRequestView view);
        HttpResponseWriter.WriteOkUtf8(ref w, "OK"u8);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(HttpParseError.None, err);
        Assert.Equal(HttpMethodKind.Get, view.Method);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void HttpResponseWriter_CachedServerAndDateHeader_ReturnsValidRfcHeaderWithZeroAllocation()
    {
        // Warmup
        _ = HttpResponseWriter.CachedServerAndDateHeader;

        long before = GC.GetAllocatedBytesForCurrentThread();
        ReadOnlySpan<byte> header = HttpResponseWriter.CachedServerAndDateHeader;
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(55, header.Length);
        Assert.True(header.StartsWith("Server: Hayabusa\r\nDate: "u8));
        Assert.Equal(0L, after - before);
    }

    private static void FeedAllExceptLastByte(ContiguousReceiveBuffer buffer, ReadOnlySpan<byte> frame)
    {
        for (int i = 0; i < frame.Length - 1; i++)
        {
            buffer.TryAppend(frame.Slice(i, 1));
        }
    }

    private static int DrainAllFramesCount(ContiguousReceiveBuffer buffer)
    {
        int count = 0;
        while (buffer.ReadableBytes > 0)
        {
            FrameParseError err = FrameParser.TryParse(buffer.ReadableSpan, 1024, out FrameSlice slice);
            if (err != FrameParseError.None)
            {
                break;
            }

            buffer.Consume(slice.ConsumedBytes);
            count++;
        }

        return count;
    }
}
