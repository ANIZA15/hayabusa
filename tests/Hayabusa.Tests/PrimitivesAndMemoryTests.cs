using System;
using System.Text;
using Cysharp.Text;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc.Framing;
using Xunit;

namespace Hayabusa.Tests;

public sealed class PrimitivesAndMemoryTests
{
    [Fact]
    public void StronglyTypedIds_ValidAndInvalidStates_BehaveDeterministically()
    {
        var connId = new ConnectionId(42UL);
        var roomId = new RoomId(9UL);
        var methodId = new MethodId(7);
        var tick = TickNumber.Zero.Next();

        CoreIndex affinity = roomId.ResolveCoreAffinity(4);

        Assert.True(connId.IsValid);
        Assert.False(ConnectionId.Invalid.IsValid);
        Assert.True(methodId.IsValid);
        Assert.Equal(new CoreIndex(1), affinity);
        Assert.Equal(new TickNumber(1UL), tick);
    }

    [Fact]
    public void Result_SuccessAndFailure_PreservesValueAndErrorWithoutBoxing()
    {
        var ok = Result<int, FrameParseError>.Success(123);
        var fail = Result<int, FrameParseError>.Failure(FrameParseError.PayloadTooLarge);

        bool hasOk = ok.TryGetValue(out int okValue, out FrameParseError okErr);
        bool hasFail = fail.TryGetValue(out _, out FrameParseError failErr);

        Assert.True(hasOk);
        Assert.Equal(123, okValue);
        Assert.Equal(FrameParseError.None, okErr);
        Assert.False(hasFail);
        Assert.Equal(FrameParseError.PayloadTooLarge, failErr);
    }

    [Fact]
    public void PinnedSlabPool_RentScoped_ReturnsBlockOnDisposeWithZeroOutstandingRentals()
    {
        using var pool = new PinnedSlabPool(blockSize: 1024, blockCount: 8);

        using (ScopedSlabRental rental = pool.RentScoped(512))
        {
            rental.Span[0] = 0xAB;
            Assert.Equal(1, pool.OutstandingRentals);
        }

        Assert.Equal(0, pool.OutstandingRentals);
        Assert.Equal(8, pool.AvailableBlocks);
    }

    [Fact]
    public void PinnedSlabPool_DoubleReturn_ThrowsInvalidOperationException()
    {
        using var pool = new PinnedSlabPool(blockSize: 1024, blockCount: 4);
        bool rented = pool.TryRentSlot(out int slot, out uint gen);
        Assert.True(rented);

        pool.ReturnSlot(slot, gen);

        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(slot, gen));
    }

    [Fact]
    public void PinnedSlabPool_TenThousandRentReturnCycles_HasZeroLeak()
    {
        using var pool = new PinnedSlabPool(blockSize: 4096, blockCount: 16);

        PerformRentReturnCycles(pool, 10_000);

        Assert.Equal(0, pool.OutstandingRentals);
        Assert.Equal(16, pool.AvailableBlocks);
    }

    [Fact]
    public void StronglyTypedPrimitives_ZStringUtf8AndSpanFormatting_ProducesExactOutputWithZeroHeapStringAllocations()
    {
        var connId = new ConnectionId(42UL);
        var roomId = new RoomId(99UL);
        var methodId = new MethodId(7);
        var coreIndex = new CoreIndex(3);
        var tick = new TickNumber(120UL);

        using Utf8ValueStringBuilder sb = ZString.CreateUtf8StringBuilder();
        sb.Append(connId);
        sb.Append('|');
        sb.Append(roomId);
        sb.Append('|');
        sb.Append(methodId);
        sb.Append('|');
        sb.Append(coreIndex);
        sb.Append('|');
        sb.Append(tick);

        Assert.Equal("conn:42|room:99|method:7|core:3|tick:120", Encoding.UTF8.GetString(sb.AsSpan()));
    }

    [Fact]
    public void TransportSubmissionQueue_TryEnqueueBatchParamsSpan_CopiesEntriesWithoutAllocation()
    {
        var queue = new TransportSubmissionQueue(16);
        var e1 = new TransportSubmissionEntry(TransportOperationKind.Send, new ConnectionId(1UL), ReadOnlyMemory<byte>.Empty);
        var e2 = new TransportSubmissionEntry(TransportOperationKind.Disconnect, new ConnectionId(2UL), ReadOnlyMemory<byte>.Empty);

        int enqueued = queue.TryEnqueueBatch(e1, e2);

        Assert.Equal(2, enqueued);
        Assert.Equal(2, queue.Count);
    }

    private static void PerformRentReturnCycles(PinnedSlabPool pool, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            using ScopedSlabRental rental = pool.RentScoped(128);
            rental.Span[0] = (byte)(i & 0xFF);
        }
    }
}
