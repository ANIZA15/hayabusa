using System;
using System.Text;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc.Framing;
using MemoryPack;
using Xunit;

namespace Hayabusa.Tests;

public sealed class CoreBuffersAndPrimitivesExtendedTests
{
    [Fact]
    public void ConnectionId_ComparisonsAndOperators_OrderByUnderlyingValue()
    {
        var low = new ConnectionId(10UL);
        var high = new ConnectionId(20UL);
        var same = new ConnectionId(10UL);

        Assert.True(low < high);
        Assert.True(low <= same);
        Assert.True(high > low);
        Assert.True(high >= same);
        Assert.True(low.CompareTo(high) < 0);
        Assert.Equal(0, low.CompareTo(same));
        Assert.Equal("conn:10", low.ToString());
        Assert.Equal("conn:10", low.ToString(null, null));
    }

    [Fact]
    public void RoomId_ComparisonsAndAffinityValidation_BehaveCorrectly()
    {
        var r1 = new RoomId(5UL);
        var r2 = new RoomId(15UL);

        Assert.False(RoomId.Invalid.IsValid);
        Assert.True(r1.IsValid);
        Assert.True(r1 < r2);
        Assert.True(r1 <= r2);
        Assert.True(r2 > r1);
        Assert.True(r2 >= r1);
        Assert.True(r1.CompareTo(r2) < 0);
        Assert.Equal("room:5", r1.ToString(null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => r1.ResolveCoreAffinity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => r1.ResolveCoreAffinity(-2));
    }

    [Fact]
    public void MethodId_FromRouteAndComparisons_ProduceDeterministicPositiveIds()
    {
        MethodId id1 = MethodId.FromRoute("BattleService", "Attack");
        MethodId id2 = MethodId.FromRoute("BattleService", "Attack");
        MethodId id3 = MethodId.FromRoute("BattleService", "Defend");

        Assert.False(MethodId.Invalid.IsValid);
        Assert.False(new MethodId(-1).IsValid);
        Assert.True(id1.IsValid);
        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.True(new MethodId(1) < new MethodId(2));
        Assert.True(new MethodId(1) <= new MethodId(1));
        Assert.True(new MethodId(2) > new MethodId(1));
        Assert.True(new MethodId(2) >= new MethodId(2));
        Assert.True(new MethodId(1).CompareTo(new MethodId(2)) < 0);
        Assert.Equal("method:7", new MethodId(7).ToString(null, null));
    }

    [Fact]
    public void CoreIndex_ValidityAndComparisons_BehaveDeterministically()
    {
        var invalid = new CoreIndex(-1);
        var zero = CoreIndex.Zero;
        var zeroCopy = CoreIndex.Zero;
        var two = new CoreIndex(2);
        var twoCopy = new CoreIndex(2);

        Assert.False(invalid.IsValid);
        Assert.True(zero.IsValid);
        Assert.True(zero < two);
        Assert.True(zero <= zeroCopy);
        Assert.True(two > zero);
        Assert.True(two >= twoCopy);
        Assert.True(zero.CompareTo(two) < 0);
        Assert.Equal("core:2", two.ToString(null, null));
    }

    [Fact]
    public void TickNumber_AdvanceAndComparisons_BehaveDeterministically()
    {
        TickNumber start = TickNumber.Zero;
        TickNumber startCopy = TickNumber.Zero;
        TickNumber advanced = start.Advance(25UL);
        TickNumber advancedCopy = new(25UL);

        Assert.Equal(new TickNumber(25UL), advanced);
        Assert.True(start < advanced);
        Assert.True(start <= startCopy);
        Assert.True(advanced > start);
        Assert.True(advanced >= advancedCopy);
        Assert.True(start.CompareTo(advanced) < 0);
        Assert.Equal("tick:25", advanced.ToString(null, null));
    }

    [Fact]
    public void Primitives_TryFormatCharAndUtf8_ReturnsFalseWhenDestinationTooSmall()
    {
        var connId = new ConnectionId(12345UL);
        var roomId = new RoomId(12345UL);
        var methodId = new MethodId(12345);
        var coreIndex = new CoreIndex(123);
        var tick = new TickNumber(12345UL);
        Span<char> tinyChars = stackalloc char[4];
        Span<byte> tinyUtf8 = stackalloc byte[4];

        Assert.False(connId.TryFormat(tinyChars, out _, default, null));
        Assert.False(connId.TryFormat(tinyUtf8, out _, default, null));
        Assert.False(roomId.TryFormat(tinyChars, out _, default, null));
        Assert.False(roomId.TryFormat(tinyUtf8, out _, default, null));
        Assert.False(methodId.TryFormat(tinyChars, out _, default, null));
        Assert.False(methodId.TryFormat(tinyUtf8, out _, default, null));
        Assert.False(coreIndex.TryFormat(tinyChars, out _, default, null));
        Assert.False(coreIndex.TryFormat(tinyUtf8, out _, default, null));
        Assert.False(tick.TryFormat(tinyChars, out _, default, null));
        Assert.False(tick.TryFormat(tinyUtf8, out _, default, null));
    }

    [Fact]
    public void Primitives_TryFormatChar_SucceedsWhenBufferIsSufficient()
    {
        Span<char> chars = stackalloc char[32];

        Assert.True(new ConnectionId(9UL).TryFormat(chars, out int cLen, default, null));
        Assert.Equal("conn:9", new string(chars.Slice(0, cLen)));

        Assert.True(new RoomId(8UL).TryFormat(chars, out int rLen, default, null));
        Assert.Equal("room:8", new string(chars.Slice(0, rLen)));

        Assert.True(new MethodId(7).TryFormat(chars, out int mLen, default, null));
        Assert.Equal("method:7", new string(chars.Slice(0, mLen)));

        Assert.True(new CoreIndex(6).TryFormat(chars, out int coreLen, default, null));
        Assert.Equal("core:6", new string(chars.Slice(0, coreLen)));

        Assert.True(new TickNumber(5UL).TryFormat(chars, out int tLen, default, null));
        Assert.Equal("tick:5", new string(chars.Slice(0, tLen)));
    }

    [Fact]
    public void Primitives_MemoryPackRoundtrip_PreservesExactValues()
    {
        var conn = new ConnectionId(777UL);
        var room = new RoomId(888UL);
        var method = new MethodId(42);
        var core = new CoreIndex(3);
        var tick = new TickNumber(999UL);

        Assert.Equal(conn, MemoryPackSerializer.Deserialize<ConnectionId>(MemoryPackSerializer.Serialize(conn)));
        Assert.Equal(room, MemoryPackSerializer.Deserialize<RoomId>(MemoryPackSerializer.Serialize(room)));
        Assert.Equal(method, MemoryPackSerializer.Deserialize<MethodId>(MemoryPackSerializer.Serialize(method)));
        Assert.Equal(core, MemoryPackSerializer.Deserialize<CoreIndex>(MemoryPackSerializer.Serialize(core)));
        Assert.Equal(tick, MemoryPackSerializer.Deserialize<TickNumber>(MemoryPackSerializer.Serialize(tick)));
    }

    [Fact]
    public void Result_EqualityHashCodeAndValueThrowOnFailure_BehaveCorrectly()
    {
        var ok1 = Result<int, FrameParseError>.Success(42);
        var ok2 = Result<int, FrameParseError>.Success(42);
        var okDiff = Result<int, FrameParseError>.Success(99);
        var err1 = Result<int, FrameParseError>.Failure(FrameParseError.InvalidMagicByte);
        var err2 = Result<int, FrameParseError>.Failure(FrameParseError.InvalidMagicByte);

        Assert.True(ok1.IsSuccess);
        Assert.False(ok1.IsFailure);
        Assert.Equal(42, ok1.Value);
        Assert.True(err1.IsFailure);
        Assert.False(err1.IsSuccess);
        Assert.Equal(FrameParseError.InvalidMagicByte, err1.Error);
        Assert.True(ok1 == ok2);
        Assert.True(ok1 != okDiff);
        Assert.True(ok1 != err1);
        Assert.True(err1 == err2);
        Assert.True(ok1.Equals((object)ok2));
        Assert.False(ok1.Equals((object)"not-a-result"));
        Assert.Equal(ok1.GetHashCode(), ok2.GetHashCode());
        Assert.Equal(err1.GetHashCode(), err2.GetHashCode());
        Assert.Throws<InvalidOperationException>(() => _ = err1.Value);
    }

    [Fact]
    public void ContiguousReceiveBuffer_CompactionCommitConsumeAndReset_MaintainDataIntegrity()
    {
        var buffer = new ContiguousReceiveBuffer(8);

        Assert.Equal(8, buffer.Capacity);
        Assert.True(buffer.TryAppend(ReadOnlySpan<byte>.Empty));
        Assert.True(buffer.TryAppend([1, 2, 3, 4, 5, 6]));
        buffer.Consume(4);
        Assert.Equal(2, buffer.ReadableBytes);

        // Appending 5 bytes requires automatic compaction of the remaining 2 bytes ([5, 6]) to index 0
        Assert.True(buffer.TryAppend([7, 8, 9, 10, 11]));
        Assert.Equal(7, buffer.ReadableBytes);
        ReadOnlySpan<byte> expectedCompacted = [5, 6, 7, 8, 9, 10, 11];
        Assert.True(buffer.ReadableSpan.SequenceEqual(expectedCompacted));
        Assert.True(buffer.ReadableMemory.Span.SequenceEqual(expectedCompacted));

        // Exceeding capacity returns false without corrupting unread bytes
        Assert.False(buffer.TryAppend([12, 13]));
        Assert.Equal(7, buffer.ReadableBytes);

        buffer.Reset();
        Assert.Equal(0, buffer.ReadableBytes);
    }

    [Fact]
    public void ContiguousReceiveBuffer_DirectWritableSpanAndInvalidOperations_EnforceContracts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContiguousReceiveBuffer(0));
        var buffer = new ContiguousReceiveBuffer(8);

        Span<byte> writable = buffer.GetWritableSpan(4);
        writable[0] = 0x11;
        writable[1] = 0x22;
        buffer.CommitWrite(2);

        Assert.Equal(2, buffer.ReadableBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CommitWrite(-1));
        Assert.Throws<InvalidOperationException>(() => buffer.CommitWrite(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Consume(-1));
        Assert.Throws<InvalidOperationException>(() => buffer.Consume(3));

        buffer.Consume(2);
        Assert.Equal(0, buffer.ReadableBytes);
        _ = buffer.GetWritableSpan(8);
    }

    [Fact]
    public void PinnedSlabPool_ExhaustionResetArenaAndInvalidSlotValidation_EnforceSafetyInvariants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinnedSlabPool(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinnedSlabPool(1024, 0));

        using var pool = new PinnedSlabPool(blockSize: 256, blockCount: 2);
        Assert.Equal(256, pool.BlockSize);
        Assert.Equal(2, pool.BlockCount);
        Assert.Equal(512, pool.TotalByteLength);
        Assert.NotEqual(0, pool.BaseAddress);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.RentScoped(512));

        ScopedSlabRental r1 = pool.RentScoped(64);
        ScopedSlabRental r2 = pool.RentScoped(64);
        r1.Memory.Span[0] = 0x42;
        Assert.Equal(0x42, r1.Span[0]);
        Assert.True(r1.SlotIndex >= 0);
        Assert.True(r1.Generation > 0);

        Assert.False(pool.TryRentSlot(out _, out _));
        Assert.Throws<InvalidOperationException>(() => pool.RentScoped(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.ReturnSlot(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.ReturnSlot(99, 1));
        int r1Slot = r1.SlotIndex;
        uint r1Gen = r1.Generation;
        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(r1Slot, r1Gen + 99));

        pool.ResetArena();
        Assert.Equal(0, pool.OutstandingRentals);
        Assert.Equal(2, pool.AvailableBlocks);

        // Old generation token from before ResetArena must now be rejected
        int staleSlot = r2.SlotIndex;
        uint staleGen = r2.Generation;
        Assert.Throws<InvalidOperationException>(() => pool.ReturnSlot(staleSlot, staleGen));

        ScopedSlabRental defaultRental = default;
        Assert.True(defaultRental.Span.IsEmpty);
        Assert.True(defaultRental.Memory.IsEmpty);
        defaultRental.Dispose();
    }

    [Fact]
    public void PinnedSlabPool_AfterDispose_ThrowsObjectDisposedOrReturnsFalse()
    {
        var pool = new PinnedSlabPool(256, 2);
        pool.Dispose();

        Assert.False(pool.TryRentSlot(out _, out _));
        Assert.Throws<ObjectDisposedException>(() => _ = pool.BaseAddress);
        Assert.Throws<ObjectDisposedException>(() => pool.RentScoped(16));
        Assert.Throws<ObjectDisposedException>(() => pool.ResetArena());
    }

    [Fact]
    public void PooledBufferWriter_DynamicGrowBackpatchingAndValidation_WorkCorrectly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledBufferWriter(0));

        using var writer = new PooledBufferWriter(initialCapacity: 16);
        Assert.Equal(16, writer.Capacity);

        int reservedOffset = writer.ReserveHeaderSpace(4);
        byte[] largePayload = new byte[64];
        largePayload.AsSpan().Fill(0x5A);
        writer.Write(largePayload);
        writer.Write(ReadOnlySpan<byte>.Empty);

        Span<byte> headerSpan = writer.GetReservedSpan(reservedOffset, 4);
        headerSpan[0] = 1;
        headerSpan[1] = 2;
        headerSpan[2] = 3;
        headerSpan[3] = 4;

        Assert.Equal(68, writer.WrittenCount);
        Assert.True(writer.Capacity >= 68);
        Assert.Equal(1, writer.WrittenSpan[0]);
        Assert.Equal(0x5A, writer.WrittenMemory.Span[4]);

        _ = writer.GetMemory(32);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Throws<InvalidOperationException>(() => writer.Advance(writer.Capacity + 1));

        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(8));
    }

    [Fact]
    public void TransportPrimitivesAndSubmissionQueue_CapacityAndFlags_BehaveAsSpecified()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransportSubmissionQueue(0));
        Assert.False(SlabSlotToken.None.HasSlot);
        Assert.False(new SlabSlotToken(0, 0).HasSlot);
        Assert.True(new SlabSlotToken(0, 1).HasSlot);

        var eofEvent = new TransportCompletionEvent(TransportOperationKind.Receive, new ConnectionId(1UL), 0, new SlabSlotToken(2, 3));
        var validRecv = new TransportCompletionEvent(TransportOperationKind.Receive, new ConnectionId(1UL), 16, new SlabSlotToken(2, 3));
        var disconnectEv = new TransportCompletionEvent(TransportOperationKind.Disconnect, new ConnectionId(1UL), 0, SlabSlotToken.None);

        Assert.True(eofEvent.IsClosedOrError);
        Assert.Equal(2, eofEvent.SlabSlotIndex);
        Assert.Equal(3u, eofEvent.SlabGeneration);
        Assert.False(validRecv.IsClosedOrError);
        Assert.True(disconnectEv.IsClosedOrError);

        var queue = new TransportSubmissionQueue(capacity: 2);
        Assert.Equal(2, queue.Capacity);
        Assert.True(queue.TryEnqueueSend(new ConnectionId(1UL), new byte[] { 1 }));
        Assert.True(queue.TryEnqueueDisconnect(new ConnectionId(2UL)));
        Assert.False(queue.TryEnqueueSend(new ConnectionId(3UL), ReadOnlyMemory<byte>.Empty));
        Assert.Equal(0, queue.TryEnqueueBatch(new TransportSubmissionEntry(TransportOperationKind.None, ConnectionId.Invalid, ReadOnlyMemory<byte>.Empty)));
        Assert.Equal(2, queue.PendingEntries.Length);

        queue.Clear();
        Assert.Equal(0, queue.Count);
    }
}
