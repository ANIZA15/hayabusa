using System;
using System.Runtime.CompilerServices;

namespace Hayabusa.Core.Buffers;

/// <summary>
/// A zero-allocation stack-only handle representing a rented block from <see cref="PinnedSlabPool"/>.
/// Automatically returns the block to the pool when disposed via <c>scoped using</c>.
/// </summary>
public readonly ref struct ScopedSlabRental
{
    private readonly PinnedSlabPool? _pool;
    private readonly int _slotIndex;
    private readonly uint _generation;

    internal ScopedSlabRental(PinnedSlabPool pool, int slotIndex, uint generation)
    {
        _pool = pool;
        _slotIndex = slotIndex;
        _generation = generation;
    }

    /// <summary>Gets the slot index in the owning <see cref="PinnedSlabPool"/>.</summary>
    public int SlotIndex => _slotIndex;

    /// <summary>Gets the generation token of this rental.</summary>
    public uint Generation => _generation;

    /// <summary>Gets the contiguous writable <see cref="Span{T}"/> of the rented slab block.</summary>
    public Span<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pool is not null ? _pool.GetSlotSpan(_slotIndex, _generation) : Span<byte>.Empty;
    }

    /// <summary>Gets the <see cref="Memory{T}"/> of the rented slab block.</summary>
    public Memory<byte> Memory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pool is not null ? _pool.GetSlotMemory(_slotIndex, _generation) : Memory<byte>.Empty;
    }

    /// <summary>Returns the rented block to the owning <see cref="PinnedSlabPool"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        _pool?.ReturnSlot(_slotIndex, _generation);
    }
}
