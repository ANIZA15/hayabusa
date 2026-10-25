using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hayabusa.Core.Buffers;

/// <summary>
/// Pre-allocated pinned contiguous memory slab pool bound to a single worker thread (Thread-per-Core).
/// Eliminates LOH fragmentation, GC pinning overhead, and multi-thread lock contention.
/// Ownership: The creator must call <see cref="Dispose"/> on shutdown; each rented block must be returned via <see cref="ScopedSlabRental"/>.
/// </summary>
public sealed class PinnedSlabPool : IDisposable
{
    private readonly byte[] _pinnedBackingBuffer;
    private readonly int[] _freeSlotStack;
    private readonly uint[] _generationBySlot;
    private readonly bool[] _rentedBySlot;
    private readonly int _blockSize;
    private readonly int _blockCount;
    private SpinLock _lock = new(enableThreadOwnerTracking: false);
    private int _freeStackTop;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PinnedSlabPool"/> class with a contiguous pinned byte array.
    /// </summary>
    /// <param name="blockSize">Size in bytes of each fixed-size slab block (e.g., 65536).</param>
    /// <param name="blockCount">Total number of blocks pre-allocated in the slab.</param>
    public PinnedSlabPool(int blockSize = 64 * 1024, int blockCount = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);

        long totalBytes = (long)blockSize * blockCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalBytes, Array.MaxLength);

        _blockSize = blockSize;
        _blockCount = blockCount;
        _pinnedBackingBuffer = GC.AllocateArray<byte>((int)totalBytes, pinned: true);
        _freeSlotStack = new int[blockCount];
        _generationBySlot = new uint[blockCount];
        _rentedBySlot = new bool[blockCount];
        _freeStackTop = blockCount;

        InitializeFreeSlots(blockCount);
    }

    /// <summary>Gets the size in bytes of each individual block.</summary>
    public int BlockSize => _blockSize;

    /// <summary>Gets the total number of blocks managed by this pool.</summary>
    public int BlockCount => _blockCount;

    /// <summary>Gets the number of currently rented (outstanding) blocks for zero-leak verification.</summary>
    public int OutstandingRentals => _blockCount - _freeStackTop;

    /// <summary>Gets the number of available blocks in the pool.</summary>
    public int AvailableBlocks => _freeStackTop;

    /// <summary>
    /// Gets the base memory address of the pinned slab for Linux <c>io_uring</c> Registered Buffers registration.
    /// </summary>
    public unsafe nint BaseAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_pinnedBackingBuffer));
        }
    }

    /// <summary>Gets the total byte length of the contiguous pinned backing buffer.</summary>
    public int TotalByteLength => _pinnedBackingBuffer.Length;

    /// <summary>
    /// Rents a scoped block from the slab pool. Must be disposed within the synchronous scope or returned explicitly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ScopedSlabRental RentScoped(int minimumSize = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumSize, _blockSize);

        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            if (_freeStackTop == 0)
            {
                throw new InvalidOperationException("PinnedSlabPool exhausted: all pre-allocated blocks are currently rented.");
            }

            int slotIndex = _freeSlotStack[--_freeStackTop];
            _rentedBySlot[slotIndex] = true;
            uint generation = ++_generationBySlot[slotIndex];
            return new ScopedSlabRental(this, slotIndex, generation);
        }
        finally
        {
            if (lockTaken)
            {
                _lock.Exit(useMemoryBarrier: false);
            }
        }
    }

    /// <summary>
    /// Attempts to rent a slot index and generation token without throwing when exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRentSlot(out int slotIndex, out uint generation)
    {
        if (_disposed)
        {
            slotIndex = -1;
            generation = 0;
            return false;
        }

        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            if (_disposed || _freeStackTop == 0)
            {
                slotIndex = -1;
                generation = 0;
                return false;
            }

            slotIndex = _freeSlotStack[--_freeStackTop];
            _rentedBySlot[slotIndex] = true;
            generation = ++_generationBySlot[slotIndex];
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                _lock.Exit(useMemoryBarrier: false);
            }
        }
    }

    /// <summary>
    /// Gets the writable <see cref="Span{T}"/> for an active rented slot and generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSlotSpan(int slotIndex, uint generation)
    {
        ValidateActiveSlot(slotIndex, generation);
        return _pinnedBackingBuffer.AsSpan(slotIndex * _blockSize, _blockSize);
    }

    /// <summary>
    /// Gets the <see cref="Memory{T}"/> for an active rented slot and generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetSlotMemory(int slotIndex, uint generation)
    {
        ValidateActiveSlot(slotIndex, generation);
        return _pinnedBackingBuffer.AsMemory(slotIndex * _blockSize, _blockSize);
    }

    /// <summary>
    /// Returns a previously rented slot to the pool, detecting double returns or stale generation tokens.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnSlot(int slotIndex, uint generation)
    {
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            ValidateActiveSlot(slotIndex, generation);
            _rentedBySlot[slotIndex] = false;
            _generationBySlot[slotIndex]++;
            _freeSlotStack[_freeStackTop++] = slotIndex;
        }
        finally
        {
            if (lockTaken)
            {
                _lock.Exit(useMemoryBarrier: false);
            }
        }
    }

    /// <summary>
    /// Resets all slots back to the free pool (Arena rewind at tick boundary when all in-flight operations completed).
    /// </summary>
    public void ResetArena()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool lockTaken = false;
        try
        {
            _lock.Enter(ref lockTaken);
            for (int i = 0; i < _blockCount; i++)
            {
                _rentedBySlot[i] = false;
                _generationBySlot[i]++;
                _freeSlotStack[i] = _blockCount - 1 - i;
            }

            _freeStackTop = _blockCount;
        }
        finally
        {
            if (lockTaken)
            {
                _lock.Exit(useMemoryBarrier: false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }

    private void InitializeFreeSlots(int blockCount)
    {
        for (int i = 0; i < blockCount; i++)
        {
            _freeSlotStack[i] = blockCount - 1 - i;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateActiveSlot(int slotIndex, uint generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)slotIndex >= (uint)_blockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        if (!_rentedBySlot[slotIndex] || _generationBySlot[slotIndex] != generation)
        {
            throw new InvalidOperationException("Double return or invalid generation token detected on PinnedSlabPool slot.");
        }
    }
}
