using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;

namespace Hayabusa.Transport;

/// <summary>
/// Deterministic in-memory completion-based transport for microsecond-level unit testing,
/// penetration testing, and zero-allocation benchmarking without OS socket overhead.
/// Ownership: Caller must call <see cref="Dispose"/> when finished.
/// </summary>
public sealed class InMemoryTransport : ITransportBackend
{
    private readonly PinnedSlabPool _slabPool;
    private readonly Queue<TransportCompletionEvent> _completionQueue = new(256);
    private readonly Dictionary<ConnectionId, List<byte[]>> _sentFramesByConnection = new();
    private readonly HashSet<ConnectionId> _disconnectedConnections = new();
    private int _submitBatchCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryTransport"/> class.
    /// </summary>
    /// <param name="slabPool">Optional pre-allocated slab pool; creates a default one if null.</param>
    public InMemoryTransport(PinnedSlabPool? slabPool = null)
    {
        _slabPool = slabPool ?? new PinnedSlabPool(64 * 1024, 128);
    }

    /// <inheritdoc />
    public string Name => "inmemory";

    /// <inheritdoc />
    public PinnedSlabPool SlabPool => _slabPool;

    /// <summary>Gets the number of batched <see cref="SubmitAndWait"/> calls invoked on this transport.</summary>
    public int SubmitBatchCount => _submitBatchCount;

    /// <summary>Gets a value indicating whether the specified connection has been disconnected.</summary>
    public bool IsDisconnected(ConnectionId connectionId) => _disconnectedConnections.Contains(connectionId);

    /// <summary>Simulates an incoming connection accept completion event.</summary>
    public void EnqueueAccept(ConnectionId connectionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _disconnectedConnections.Remove(connectionId);
        _completionQueue.Enqueue(new TransportCompletionEvent(
            TransportOperationKind.Accept,
            connectionId,
            0,
            SlabSlotToken.None));
    }

    /// <summary>
    /// Copies <paramref name="payload"/> into a rented pinned slab slot and enqueues a Receive CQE.
    /// </summary>
    public bool TryEnqueueReceive(ConnectionId connectionId, scoped ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_slabPool.TryRentSlot(out int slotIndex, out uint generation))
        {
            return false;
        }

        Span<byte> slotSpan = _slabPool.GetSlotSpan(slotIndex, generation);
        if (payload.Length > slotSpan.Length)
        {
            _slabPool.ReturnSlot(slotIndex, generation);
            return false;
        }

        payload.CopyTo(slotSpan);
        _completionQueue.Enqueue(new TransportCompletionEvent(
            TransportOperationKind.Receive,
            connectionId,
            payload.Length,
            new SlabSlotToken(slotIndex, generation)));
        return true;
    }

    /// <summary>Simulates a peer disconnect event.</summary>
    public void EnqueueDisconnect(ConnectionId connectionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _disconnectedConnections.Add(connectionId);
        _completionQueue.Enqueue(new TransportCompletionEvent(
            TransportOperationKind.Disconnect,
            connectionId,
            0,
            SlabSlotToken.None));
    }

    /// <summary>Gets all transmitted payloads recorded for <paramref name="connectionId"/>.</summary>
    public IReadOnlyList<byte[]> GetSentPayloads(ConnectionId connectionId)
    {
        return _sentFramesByConnection.TryGetValue(connectionId, out List<byte[]>? list)
            ? list
            : Array.Empty<byte[]>();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainCompletions(Span<TransportCompletionEvent> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = 0;
        while (count < destination.Length && _completionQueue.TryDequeue(out TransportCompletionEvent ev))
        {
            destination[count++] = ev;
        }

        return count;
    }

    /// <inheritdoc />
    public int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _submitBatchCount++;

        for (int i = 0; i < submissions.Length; i++)
        {
            ProcessSingleSubmission(in submissions[i]);
        }

        return submissions.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _slabPool.Dispose();
    }

    private void ProcessSingleSubmission(in TransportSubmissionEntry entry)
    {
        if (entry.Operation == TransportOperationKind.Send)
        {
            RecordSentPayload(entry.ConnectionId, entry.Payload.Span);
        }
        else if (entry.Operation == TransportOperationKind.Disconnect)
        {
            _disconnectedConnections.Add(entry.ConnectionId);
        }

        if (entry.SlabToken.HasSlot)
        {
            _slabPool.ReturnSlot(entry.SlabToken.SlotIndex, entry.SlabToken.Generation);
        }
    }

    private void RecordSentPayload(ConnectionId connectionId, scoped ReadOnlySpan<byte> payload)
    {
        if (!_sentFramesByConnection.TryGetValue(connectionId, out List<byte[]>? list))
        {
            list = new List<byte[]>(8);
            _sentFramesByConnection[connectionId] = list;
        }

        list.Add(payload.ToArray());
    }
}
