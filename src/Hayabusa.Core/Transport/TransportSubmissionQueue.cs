using System;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Core.Transport;

/// <summary>
/// Thread-local Submission Queue (SQE accumulator) that batches all outgoing send/receive/disconnect requests
/// within a worker loop iteration so they can be flushed in a single kernel submission (<c>io_uring_enter</c> or batched socket flush).
/// Not thread-safe; bound to a single <see cref="CoreIndex"/>.
/// </summary>
public sealed class TransportSubmissionQueue
{
    private readonly TransportSubmissionEntry[] _entries;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportSubmissionQueue"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of SQEs buffered before an automatic flush is needed.</param>
    public TransportSubmissionQueue(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new TransportSubmissionEntry[capacity];
    }

    /// <summary>Gets the number of pending SQEs currently queued.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Gets the maximum capacity of this submission queue.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Gets a read-only span of all pending submission entries.</summary>
    public ReadOnlySpan<TransportSubmissionEntry> PendingEntries
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entries.AsSpan(0, _count);
    }

    /// <summary>Enqueues a send request into the local SQE batch without issuing an immediate syscall.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueueSend(ConnectionId connectionId, ReadOnlyMemory<byte> payload, SlabSlotToken slabToken = default)
    {
        return TryEnqueue(new TransportSubmissionEntry(
            TransportOperationKind.Send,
            connectionId,
            payload,
            slabToken));
    }

    /// <summary>Enqueues a disconnect request into the local SQE batch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueueDisconnect(ConnectionId connectionId)
    {
        return TryEnqueue(new TransportSubmissionEntry(
            TransportOperationKind.Disconnect,
            connectionId,
            ReadOnlyMemory<byte>.Empty,
            SlabSlotToken.None));
    }

    /// <summary>Enqueues an arbitrary submission entry into the local batch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in TransportSubmissionEntry entry)
    {
        if (_count >= _entries.Length)
        {
            return false;
        }

        _entries[_count++] = entry;
        return true;
    }

    /// <summary>Clears all queued entries after flushing to the transport backend.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }
}
