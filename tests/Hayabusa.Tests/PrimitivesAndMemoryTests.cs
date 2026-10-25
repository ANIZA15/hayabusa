using System;
using System.Buffers.Binary;
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
    public void PinnedSlabPool_WhenExhausted_TryRentSlotReturnsFalseAndRecoversImmediatelyAfterReturn()
    {
        using var pool = new PinnedSlabPool(blockSize: 256, blockCount: 2);

        Assert.True(pool.TryRentSlot(out int slot0, out uint gen0));
        Assert.True(pool.TryRentSlot(out int slot1, out uint gen1));
        Assert.False(pool.TryRentSlot(out _, out _));
        Assert.Throws<InvalidOperationException>(() => pool.RentScoped(64));

        pool.ReturnSlot(slot0, gen0);
        Assert.True(pool.TryRentSlot(out int slotReused, out uint genReused));

        pool.ReturnSlot(slot1, gen1);
        pool.ReturnSlot(slotReused, genReused);
        Assert.Equal(0, pool.OutstandingRentals);
    }

    [Fact]
    public void PinnedSlabPool_StaleGenerationTokenAfterReRent_PreventsAbaMemoryCorruption()
    {
        using var pool = new PinnedSlabPool(blockSize: 128, blockCount: 1);

        Assert.True(pool.TryRentSlot(out int slot, out uint staleGen));
        pool.ReturnSlot(slot, staleGen);

        Assert.True(pool.TryRentSlot(out int reRentedSlot, out uint activeGen));
        Assert.Equal(slot, reRentedSlot);
        Assert.NotEqual(staleGen, activeGen);

        pool.GetSlotSpan(reRentedSlot, activeGen)[0] = 0x7F;
        Assert.Throws<InvalidOperationException>(() => pool.GetSlotMemory(slot, staleGen));
        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(slot, staleGen));
        Assert.Equal(0x7F, pool.GetSlotSpan(reRentedSlot, activeGen)[0]);

        pool.ReturnSlot(reRentedSlot, activeGen);
    }

    [Fact]
    public void PinnedSlabPool_ResetArena_ReclaimsAllBlocksAndInvalidatesInFlightGenerations()
    {
        using var pool = new PinnedSlabPool(blockSize: 512, blockCount: 4);
        Assert.True(pool.TryRentSlot(out int s0, out uint g0));
        Assert.True(pool.TryRentSlot(out int s1, out uint g1));
        Assert.Equal(2, pool.OutstandingRentals);

        pool.ResetArena();

        Assert.Equal(0, pool.OutstandingRentals);
        Assert.Equal(4, pool.AvailableBlocks);
        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(s0, g0));
        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(s1, g1));
    }

    [Fact]
    public void PinnedSlabPool_DistinctSlots_ProvideNonOverlappingPinnedSlicesAndValidBaseAddress()
    {
        using var pool = new PinnedSlabPool(blockSize: 64, blockCount: 4);
        Assert.NotEqual((nint)0, pool.BaseAddress);
        Assert.Equal(256, pool.TotalByteLength);

        using ScopedSlabRental r0 = pool.RentScoped(64);
        using ScopedSlabRental r1 = pool.RentScoped(64);
        r0.Span.Fill(0x11);
        r1.Span.Fill(0x22);

        Assert.Equal(0x11, r0.Memory.Span[63]);
        Assert.Equal(0x22, r1.Memory.Span[0]);
    }

    [Fact]
    public void PinnedSlabPool_AfterDispose_RejectsOperationsAndTryRentReturnsFalse()
    {
        var pool = new PinnedSlabPool(blockSize: 128, blockCount: 2);
        pool.Dispose();

        Assert.False(pool.TryRentSlot(out _, out _));
        Assert.Throws<ObjectDisposedException>(() => _ = pool.BaseAddress);
        Assert.Throws<ObjectDisposedException>(() => pool.ResetArena());
    }

    [Fact]
    public void PooledBufferWriter_ReserveHeaderSpaceAndBackpatchAfterDynamicGrow_PreservesFullIntegrity()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 32);
        int headerOffset = writer.ReserveHeaderSpace(RpcHeader.Size);

        byte[] largePayload = new byte[1024];
        largePayload.AsSpan().Fill(0x5A);
        writer.Write(largePayload);

        var header = new RpcHeader(largePayload.Length, RpcHeader.CurrentVersion, RpcFrameFlags.Response, new MethodId(42));
        header.WriteTo(writer.GetReservedSpan(headerOffset, RpcHeader.Size));

        Assert.True(writer.Capacity >= 1036);
        FrameParseError err = FrameParser.TryParse(writer.WrittenSpan, 4096, out FrameSlice parsed);
        Assert.Equal(FrameParseError.None, err);
        Assert.Equal(new MethodId(42), parsed.Header.MethodId);
        Assert.Equal(1024, parsed.Payload.Length);
        Assert.Equal(0x5A, parsed.Payload[1023]);
    }

    [Fact]
    public void PooledBufferWriter_WarmWriteAndClearCycle_AllocatesZeroBytes()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 1024);
        ReadOnlySpan<byte> sample = "HayabusaZeroAllocPayload"u8;

        // Warmup
        writer.Write(sample);
        writer.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            writer.Write(sample);
            Span<byte> tail = writer.GetSpan(8);
            BinaryPrimitives.WriteInt64LittleEndian(tail, i);
            writer.Advance(8);
            writer.Clear();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, writer.WrittenCount);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void PooledBufferWriter_InvalidAdvanceOrDisposedAccess_ThrowsExpectedExceptions()
    {
        var writer = new PooledBufferWriter(initialCapacity: 64);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Throws<InvalidOperationException>(() => writer.Advance(65));

        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(16));
    }

    [Fact]
    public void ContiguousReceiveBuffer_PartialConsumeAndAppend_CompactsUnreadBytesWithoutCorruption()
    {
        var buffer = new ContiguousReceiveBuffer(capacity: 16);
        Assert.True(buffer.TryAppend([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));

        // Consume 10 bytes, leaving [11, 12] at offset 10..12
        buffer.Consume(10);
        Assert.Equal(2, buffer.ReadableBytes);

        // Appending 10 more bytes requires auto-compaction of [11, 12] to index 0..2
        Assert.True(buffer.TryAppend([21, 22, 23, 24, 25, 26, 27, 28, 29, 30]));
        Assert.Equal(12, buffer.ReadableBytes);
        Assert.Equal(11, buffer.ReadableSpan[0]);
        Assert.Equal(12, buffer.ReadableSpan[1]);
        Assert.Equal(30, buffer.ReadableSpan[11]);
    }

    [Fact]
    public void ContiguousReceiveBuffer_OverflowAndInvalidConsume_ProtectsExistingUnreadData()
    {
        var buffer = new ContiguousReceiveBuffer(capacity: 8);
        Assert.True(buffer.TryAppend([10, 20, 30, 40, 50]));
        Assert.False(buffer.TryAppend([60, 70, 80, 90]));

        Assert.Equal(5, buffer.ReadableBytes);
        Assert.Equal(10, buffer.ReadableSpan[0]);
        Assert.Equal(50, buffer.ReadableSpan[4]);
        Assert.Throws<InvalidOperationException>(() => buffer.Consume(6));
        Assert.Throws<InvalidOperationException>(() => buffer.CommitWrite(10));

        buffer.Consume(5);
        Assert.Equal(0, buffer.ReadableBytes);
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
    public void MethodId_FromRoute_ComputesDeterministicPositiveHashWithZeroAllocation()
    {
        // Warmup
        _ = MethodId.FromRoute("BattleRoomService", "Teleport");

        long before = GC.GetAllocatedBytesForCurrentThread();
        MethodId id1 = MethodId.FromRoute("BattleRoomService", "Teleport");
        MethodId id2 = MethodId.FromRoute("BattleRoomService", "Teleport");
        MethodId id3 = MethodId.FromRoute("BattleRoomService", "MoveAsync");
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(id1.IsValid);
        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void TransportSubmissionQueue_CapacityLimitAndWarmReuse_AllocatesZeroBytes()
    {
        var queue = new TransportSubmissionQueue(4);
        var e1 = new TransportSubmissionEntry(TransportOperationKind.Send, new ConnectionId(1UL), ReadOnlyMemory<byte>.Empty);
        var e2 = new TransportSubmissionEntry(TransportOperationKind.Disconnect, new ConnectionId(2UL), ReadOnlyMemory<byte>.Empty);

        int enqueued = queue.TryEnqueueBatch(e1, e2, e1, e2, e1);
        Assert.Equal(4, enqueued);
        Assert.False(queue.TryEnqueueDisconnect(new ConnectionId(99UL)));
        queue.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = queue.TryEnqueueSend(new ConnectionId(1UL), ReadOnlyMemory<byte>.Empty);
        _ = queue.TryEnqueueDisconnect(new ConnectionId(2UL));
        queue.Clear();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, queue.Count);
        Assert.Equal(0L, after - before);
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
