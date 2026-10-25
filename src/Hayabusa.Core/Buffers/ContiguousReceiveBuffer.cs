using System;
using System.Runtime.CompilerServices;

namespace Hayabusa.Core.Buffers;

/// <summary>
/// Contiguous single-segment receive accumulation buffer that avoids Kestrel's <c>ReadOnlySequence&lt;byte&gt;</c>
/// multi-segment linked list overhead for frames up to <see cref="Capacity"/>.
/// Bound to a single connection on a single worker thread.
/// </summary>
public sealed class ContiguousReceiveBuffer
{
    private readonly byte[] _buffer;
    private int _readOffset;
    private int _writeOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContiguousReceiveBuffer"/> class.
    /// </summary>
    /// <param name="capacity">Maximum contiguous frame buffer capacity in bytes.</param>
    public ContiguousReceiveBuffer(int capacity = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = GC.AllocateArray<byte>(capacity, pinned: true);
    }

    /// <summary>Gets the maximum capacity of this buffer.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets the number of unconsumed readable bytes currently in the buffer.</summary>
    public int ReadableBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _writeOffset - _readOffset;
    }

    /// <summary>Gets the contiguous span of unconsumed readable bytes.</summary>
    public ReadOnlySpan<byte> ReadableSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.AsSpan(_readOffset, _writeOffset - _readOffset);
    }

    /// <summary>Gets the contiguous memory slice of unconsumed readable bytes without copying.</summary>
    public ReadOnlyMemory<byte> ReadableMemory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.AsMemory(_readOffset, _writeOffset - _readOffset);
    }

    /// <summary>Gets the writable span at the tail of the buffer, compacting unread bytes to the front if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetWritableSpan(int minimumBytes = 1)
    {
        if (_buffer.Length - _writeOffset < minimumBytes)
        {
            Compact();
        }

        return _buffer.AsSpan(_writeOffset);
    }

    /// <summary>Appends incoming bytes into the buffer, returning false if capacity would be exceeded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAppend(scoped ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        Span<byte> writable = GetWritableSpan(data.Length);
        if (writable.Length < data.Length)
        {
            return false;
        }

        data.CopyTo(writable);
        _writeOffset += data.Length;
        return true;
    }

    /// <summary>Advances the write cursor after an external write into <see cref="GetWritableSpan"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite(int bytesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten);
        if (_writeOffset + bytesWritten > _buffer.Length)
        {
            throw new InvalidOperationException("CommitWrite exceeds buffer capacity.");
        }

        _writeOffset += bytesWritten;
    }

    /// <summary>Consumes <paramref name="bytesConsumed"/> from the readable head of the buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Consume(int bytesConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesConsumed);
        if (bytesConsumed > ReadableBytes)
        {
            throw new InvalidOperationException("Cannot consume more bytes than ReadableBytes.");
        }

        _readOffset += bytesConsumed;
        if (_readOffset == _writeOffset)
        {
            _readOffset = 0;
            _writeOffset = 0;
        }
    }

    /// <summary>Resets all read and write offsets to zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _readOffset = 0;
        _writeOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Compact()
    {
        int unread = _writeOffset - _readOffset;
        if (unread == 0)
        {
            _readOffset = 0;
            _writeOffset = 0;
            return;
        }

        if (_readOffset > 0)
        {
            _buffer.AsSpan(_readOffset, unread).CopyTo(_buffer);
            _readOffset = 0;
            _writeOffset = unread;
        }
    }
}
