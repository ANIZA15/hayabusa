using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;

namespace Hayabusa.Transport;

/// <summary>
/// Cross-platform completion-based transport backed by non-blocking sockets and pre-allocated pinned slab buffers.
/// Serves as the default fallback on Windows (<c>IOCP</c>), macOS (<c>kqueue</c>), and restricted Linux containers.
/// Ownership: Caller must call <see cref="Dispose"/> to close active sockets and release the slab pool.
/// </summary>
public sealed class SocketEventTransport : ITransportBackend
{
    private readonly PinnedSlabPool _slabPool;
    private readonly ConcurrentQueue<TransportCompletionEvent> _completions = new();
    private readonly ConcurrentDictionary<ConnectionId, Socket> _connections = new();
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private Socket? _listenerSocket;
    private ulong _nextConnectionSequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SocketEventTransport"/> class.
    /// </summary>
    /// <param name="slabPool">Optional pre-allocated pinned slab pool.</param>
    public SocketEventTransport(PinnedSlabPool? slabPool = null)
    {
        _slabPool = slabPool ?? new PinnedSlabPool(64 * 1024, 128);
    }

    /// <inheritdoc />
    public string Name => "socket";

    /// <inheritdoc />
    public PinnedSlabPool SlabPool => _slabPool;

    /// <summary>
    /// Binds and starts listening on the specified local endpoint if TCP socket listening is requested.
    /// </summary>
    public void BindAndListen(IPEndPoint endPoint, int backlog = 512)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endPoint);

        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            Blocking = false,
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(endPoint);
        socket.Listen(backlog);
        _listenerSocket = socket;
    }

    /// <inheritdoc />
    public int DrainCompletions(Span<TransportCompletionEvent> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PollListenerAccepts();
        PollConnectedSockets();

        int count = 0;
        while (count < destination.Length && _completions.TryDequeue(out TransportCompletionEvent ev))
        {
            destination[count++] = ev;
        }

        return count;
    }

    /// <inheritdoc />
    public int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < submissions.Length; i++)
        {
            ExecuteSubmission(in submissions[i]);
        }

        if (_completions.IsEmpty && timeout > TimeSpan.Zero)
        {
            WaitWithDeadline(timeout);
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
        _listenerSocket?.Dispose();
        foreach (Socket client in _connections.Values)
        {
            client.Dispose();
        }

        _connections.Clear();
        _wakeSignal.Dispose();
        _slabPool.Dispose();
    }

    private void PollListenerAccepts()
    {
        Socket? listener = _listenerSocket;
        if (listener is null)
        {
            return;
        }

        try
        {
            while (listener.Poll(0, SelectMode.SelectRead))
            {
                Socket client = listener.Accept();
                client.NoDelay = true;
                client.Blocking = false;
                var id = new ConnectionId(Interlocked.Increment(ref _nextConnectionSequence));
                _connections[id] = client;
                _completions.Enqueue(new TransportCompletionEvent(TransportOperationKind.Accept, id, 0, SlabSlotToken.None));
            }
        }
        catch (SocketException)
        {
            // Non-blocking EWOULDBLOCK or transient accept error; handled on next tick.
        }
    }

    private void PollConnectedSockets()
    {
        foreach (var pair in _connections)
        {
            PollSingleClient(pair.Key, pair.Value);
        }
    }

    private void PollSingleClient(ConnectionId id, Socket socket)
    {
        try
        {
            if (!socket.Poll(0, SelectMode.SelectRead))
            {
                return;
            }

            if (!_slabPool.TryRentSlot(out int slot, out uint gen))
            {
                return;
            }

            Span<byte> buffer = _slabPool.GetSlotSpan(slot, gen);
            int read = socket.Receive(buffer, SocketFlags.None);
            if (read <= 0)
            {
                _slabPool.ReturnSlot(slot, gen);
                CloseConnection(id);
                return;
            }

            _completions.Enqueue(new TransportCompletionEvent(
                TransportOperationKind.Receive,
                id,
                read,
                new SlabSlotToken(slot, gen)));
        }
        catch (SocketException)
        {
            CloseConnection(id);
        }
    }

    private void ExecuteSubmission(in TransportSubmissionEntry entry)
    {
        if (entry.Operation == TransportOperationKind.Send)
        {
            SendToSocket(entry.ConnectionId, entry.Payload.Span);
        }
        else if (entry.Operation == TransportOperationKind.Disconnect)
        {
            CloseConnection(entry.ConnectionId);
        }

        if (entry.SlabToken.HasSlot)
        {
            _slabPool.ReturnSlot(entry.SlabToken.SlotIndex, entry.SlabToken.Generation);
        }
    }

    private void SendToSocket(ConnectionId id, scoped ReadOnlySpan<byte> payload)
    {
        if (!_connections.TryGetValue(id, out Socket? socket))
        {
            return;
        }

        try
        {
            socket.Send(payload, SocketFlags.None);
        }
        catch (SocketException)
        {
            CloseConnection(id);
        }
    }

    private void CloseConnection(ConnectionId id)
    {
        if (_connections.TryRemove(id, out Socket? socket))
        {
            socket.Dispose();
            _completions.Enqueue(new TransportCompletionEvent(
                TransportOperationKind.Disconnect,
                id,
                0,
                SlabSlotToken.None));
        }
    }

    private void WaitWithDeadline(TimeSpan timeout)
    {
        int waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 0, 15);
        if (waitMs > 0)
        {
            _wakeSignal.Wait(waitMs);
        }
    }
}
