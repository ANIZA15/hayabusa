using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Hayabusa.Core.Buffers;

/// <summary>
/// Reusable high-performance <see cref="IBufferWriter{T}"/> backed by a pre-allocated pinned byte array.
/// Designed for zero-allocation serialization with <c>MemoryPackSerializer</c> and <c>ZString</c> on a single worker thread.
/// Not thread-safe; bound to a single <see cref="Primitives.CoreIndex"/>.
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly int _initialCapacity;
    private readonly int _maxRetainedCapacity;
    private byte[] _buffer;
    private int _writtenCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledBufferWriter"/> class with a pinned initial capacity
    /// and a Kestrel-style high-water mark (<paramref name="maxRetainedCapacity"/>) to prevent permanent LOH bloat after burst writes.
    /// </summary>
    /// <param name="initialCapacity">Initial buffer capacity in bytes (default: 64 KB).</param>
    /// <param name="maxRetainedCapacity">Maximum capacity retained across <see cref="Clear"/> calls (0 = 4x initialCapacity, min 256 KB).</param>
    public PooledBufferWriter(int initialCapacity = 64 * 1024, int maxRetainedCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _initialCapacity = initialCapacity;
        _maxRetainedCapacity = maxRetainedCapacity > 0
            ? Math.Max(initialCapacity, maxRetainedCapacity)
            : Math.Max(initialCapacity * 4, 256 * 1024);
        _buffer = GC.AllocateArray<byte>(initialCapacity, pinned: true);
    }

    /// <summary>Gets the high-water mark capacity above which <see cref="Clear"/> releases the oversized buffer.</summary>
    public int MaxRetainedCapacity => _maxRetainedCapacity;

    /// <summary>Gets the number of bytes written to the current buffer.</summary>
    public int WrittenCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _writtenCount;
    }

    /// <summary>Gets the total capacity of the backing buffer.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets the read-only span of bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.AsSpan(0, _writtenCount);
    }

    /// <summary>Gets the read-only memory of bytes written so far.</summary>
    public ReadOnlyMemory<byte> WrittenMemory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.AsMemory(0, _writtenCount);
    }

    /// <summary>
    /// Reserves <paramref name="headerSize"/> bytes at the current write position and returns the offset
    /// so the caller can backpatch the header after serializing a variable-length payload directly into this buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReserveHeaderSpace(int headerSize)
    {
        EnsureCapacity(headerSize);
        int offset = _writtenCount;
        _writtenCount += headerSize;
        return offset;
    }

    /// <summary>
    /// Gets a writable span at a previously reserved offset for length backpatching (even if the backing array grew during payload serialization).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetReservedSpan(int offset, int length)
    {
        return _buffer.AsSpan(offset, length);
    }

    /// <summary>
    /// Resets the write cursor to zero without deallocating the backing buffer, unless the buffer grew past
    /// <see cref="MaxRetainedCapacity"/> (in which case it shrinks back to initial capacity to prevent LOH retention).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _writtenCount = 0;
        if (_buffer.Length > _maxRetainedCapacity)
        {
            ShrinkToInitialCapacity();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShrinkToInitialCapacity()
    {
        _buffer = GC.AllocateArray<byte>(_initialCapacity, pinned: true);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_writtenCount + count > _buffer.Length)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        _writtenCount += count;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_writtenCount);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_writtenCount);
    }

    /// <summary>Writes raw bytes directly into the buffer without intermediate allocations.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(scoped ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        Span<byte> destination = GetSpan(source.Length);
        source.CopyTo(destination);
        _writtenCount += source.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _writtenCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        int required = sizeHint == 0 ? 256 : sizeHint;
        int available = _buffer.Length - _writtenCount;
        if (available >= required)
        {
            return;
        }

        Grow(required);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int required)
    {
        int newCapacity = Math.Max(_buffer.Length * 2, _writtenCount + required);
        byte[] newBuffer = GC.AllocateArray<byte>(newCapacity, pinned: true);
        _buffer.AsSpan(0, _writtenCount).CopyTo(newBuffer);
        _buffer = newBuffer;
    }
}
