using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;

namespace Hayabusa.Transport;

/// <summary>
/// Cross-platform $O(1)$ completion-driven transport backed by OS <c>epoll</c> (Linux), <c>IOCP</c> (Windows),
/// or <c>kqueue</c> (macOS) via pre-allocated <see cref="SocketAsyncEventArgs"/> and pinned slab buffers.
/// Eliminates $O(N)$ socket polling and supports <c>SO_REUSEPORT</c> multi-core parallel accepts and multi-port listeners.
/// Ownership: Caller must call <see cref="Dispose"/> to close active sockets and release the slab pool.
/// </summary>
public sealed class SocketEventTransport : ITransportBackend
{
    private const int LinuxSolSocket = 1;
    private const int LinuxSoReusePort = 15;

    private readonly PinnedSlabPool _slabPool;
    private readonly ConcurrentQueue<TransportCompletionEvent> _completions = new();
    private readonly ConcurrentQueue<ClientSession> _pendingRearmQueue = new();
    private readonly ConcurrentDictionary<ConnectionId, ClientSession> _sessions = new();
    private readonly ConcurrentBag<SocketAsyncEventArgs> _receiveEventArgsPool = new();
    private readonly List<Socket> _listenerSockets = new(2);
    private readonly List<SocketAsyncEventArgs> _acceptEventArgs = new(2);
    private readonly ManualResetEventSlim _wakeSignal = new(false, spinCount: 1024);
    private static ulong _globalConnectionSequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SocketEventTransport"/> class.
    /// </summary>
    /// <param name="slabPool">Optional pre-allocated pinned slab pool.</param>
    public SocketEventTransport(PinnedSlabPool? slabPool = null)
    {
        _slabPool = slabPool ?? new PinnedSlabPool(64 * 1024, 128);
        PreallocateReceiveEventArgs(Math.Min(128, _slabPool.BlockCount));
    }

    /// <inheritdoc />
    public string Name => "socket";

    /// <inheritdoc />
    public PinnedSlabPool SlabPool => _slabPool;

    /// <summary>Gets the number of active listener sockets bound on this transport.</summary>
    public int ListenerCount => _listenerSockets.Count;

    /// <summary>
    /// Binds and starts listening on the specified local endpoint using $O(1)$ asynchronous accept completions
    /// and optional <c>SO_REUSEPORT</c> for multi-core Thread-per-Core scaling.
    /// </summary>
    public void BindAndListen(IPEndPoint endPoint, int backlog = 4096, bool enableReusePort = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endPoint);

        var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (enableReusePort)
        {
            TryEnableReusePort(listener);
        }

        listener.Bind(endPoint);
        listener.Listen(backlog);
        _listenerSockets.Add(listener);

        var acceptArgs = new SocketAsyncEventArgs { UserToken = listener };
        acceptArgs.Completed += OnAcceptCompleted;
        _acceptEventArgs.Add(acceptArgs);
        IssueAcceptAsync(listener, acceptArgs);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainCompletions(Span<TransportCompletionEvent> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = 0;
        while (count < destination.Length && _completions.TryDequeue(out TransportCompletionEvent ev))
        {
            destination[count++] = ev;
            if (ev.Operation == TransportOperationKind.Receive && _sessions.TryGetValue(ev.ConnectionId, out ClientSession? session))
            {
                ScheduleReceiveRearm(session);
            }
        }

        return count;
    }

    /// <inheritdoc />
    public int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RearmPendingReceives();

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
        _wakeSignal.Set();

        for (int i = 0; i < _listenerSockets.Count; i++)
        {
            _listenerSockets[i].Dispose();
        }

        for (int i = 0; i < _acceptEventArgs.Count; i++)
        {
            _acceptEventArgs[i].Dispose();
        }

        foreach (ClientSession session in _sessions.Values)
        {
            session.Dispose();
        }

        while (_receiveEventArgsPool.TryTake(out SocketAsyncEventArgs? pooledArgs))
        {
            pooledArgs.Dispose();
        }

        _sessions.Clear();
        _listenerSockets.Clear();
        _acceptEventArgs.Clear();
        _wakeSignal.Dispose();
        _slabPool.Dispose();
    }

    private void PreallocateReceiveEventArgs(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var args = new SocketAsyncEventArgs();
            args.Completed += OnReceiveCompleted;
            _receiveEventArgsPool.Add(args);
        }
    }

    private SocketAsyncEventArgs RentReceiveEventArgs()
    {
        if (_receiveEventArgsPool.TryTake(out SocketAsyncEventArgs? args))
        {
            return args;
        }

        var created = new SocketAsyncEventArgs();
        created.Completed += OnReceiveCompleted;
        return created;
    }

    private void ReturnReceiveEventArgs(SocketAsyncEventArgs args)
    {
        args.UserToken = null;
        args.SetBuffer(Memory<byte>.Empty);
        if (!_disposed)
        {
            _receiveEventArgsPool.Add(args);
        }
        else
        {
            args.Dispose();
        }
    }

    private static void TryEnableReusePort(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            socket.SetRawSocketOption(LinuxSolSocket, LinuxSoReusePort, [1, 0, 0, 0]);
        }
        catch (SocketException)
        {
            // Kernel or container policy does not permit SO_REUSEPORT; proceed with standard ReuseAddress.
        }
    }

    private void IssueAcceptAsync(Socket listener, SocketAsyncEventArgs acceptArgs)
    {
        while (!_disposed)
        {
            acceptArgs.AcceptSocket = null;
            try
            {
                if (listener.AcceptAsync(acceptArgs))
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            if (!ProcessAcceptedSocket(acceptArgs))
            {
                return;
            }
        }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (ProcessAcceptedSocket(e) && e.UserToken is Socket listener)
        {
            IssueAcceptAsync(listener, e);
        }
    }

    private bool ProcessAcceptedSocket(SocketAsyncEventArgs e)
    {
        if (e.SocketError != SocketError.Success || e.AcceptSocket is null)
        {
            e.AcceptSocket?.Dispose();
            return !_disposed;
        }

        Socket client = e.AcceptSocket;
        client.NoDelay = true;
        var id = new ConnectionId(Interlocked.Increment(ref _globalConnectionSequence));
        SocketAsyncEventArgs receiveArgs = RentReceiveEventArgs();

        var session = new ClientSession(id, client, receiveArgs, ReturnReceiveEventArgs);
        _sessions[id] = session;
        EnqueueCompletionAndWake(new TransportCompletionEvent(TransportOperationKind.Accept, id, 0, SlabSlotToken.None));
        TryStartReceiveAsync(session);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ScheduleReceiveRearm(ClientSession session)
    {
        if (session.MarkArmedForNextReceive())
        {
            _pendingRearmQueue.Enqueue(session);
        }
    }

    private void RearmPendingReceives()
    {
        while (_pendingRearmQueue.TryDequeue(out ClientSession? session))
        {
            if (session.ConsumeNeedsRearm())
            {
                TryStartReceiveAsync(session);
            }
        }
    }

    private void TryStartReceiveAsync(ClientSession session)
    {
        while (!_disposed && !session.IsClosed)
        {
            if (!_slabPool.TryRentSlot(out int slot, out uint gen))
            {
                ScheduleReceiveRearm(session);
                return;
            }

            if (!IssueSingleReceiveOnSlot(session, slot, gen))
            {
                return;
            }
        }
    }

    private bool IssueSingleReceiveOnSlot(ClientSession session, int slot, uint gen)
    {
        Memory<byte> memory = _slabPool.GetSlotMemory(slot, gen);
        session.CurrentSlot = new SlabSlotToken(slot, gen);
        session.ReceiveArgs.SetBuffer(memory);

        try
        {
            if (session.Socket.ReceiveAsync(session.ReceiveArgs))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _slabPool.ReturnSlot(slot, gen);
            session.CurrentSlot = SlabSlotToken.None;
            CloseConnection(session.Id);
            return false;
        }

        return CompleteReceiveSyncOrAsync(session, session.ReceiveArgs);
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (e.UserToken is ClientSession session)
        {
            _ = CompleteReceiveSyncOrAsync(session, e);
        }
    }

    private bool CompleteReceiveSyncOrAsync(ClientSession session, SocketAsyncEventArgs e)
    {
        SlabSlotToken slot = session.CurrentSlot;
        session.CurrentSlot = SlabSlotToken.None;

        int bytesTransferred = e.BytesTransferred;
        if (_disposed || e.SocketError != SocketError.Success || bytesTransferred <= 0)
        {
            if (slot.HasSlot)
            {
                _slabPool.ReturnSlot(slot.SlotIndex, slot.Generation);
            }

            CloseConnection(session.Id);
            return false;
        }

        EnqueueCompletionAndWake(new TransportCompletionEvent(
            TransportOperationKind.Receive,
            session.Id,
            bytesTransferred,
            slot));

        // Wait until the worker drains and processes this CQE before issuing the next Receive on this connection
        return false;
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
        if (!_sessions.TryGetValue(id, out ClientSession? session) || session.IsClosed)
        {
            return;
        }

        try
        {
            int sent = 0;
            while (sent < payload.Length)
            {
                int n = session.Socket.Send(payload.Slice(sent), SocketFlags.None);
                if (n <= 0)
                {
                    CloseConnection(id);
                    return;
                }

                sent += n;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            CloseConnection(id);
        }
    }

    private void CloseConnection(ConnectionId id)
    {
        if (_sessions.TryRemove(id, out ClientSession? session))
        {
            session.Dispose();
            EnqueueCompletionAndWake(new TransportCompletionEvent(
                TransportOperationKind.Disconnect,
                id,
                0,
                SlabSlotToken.None));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnqueueCompletionAndWake(in TransportCompletionEvent ev)
    {
        _completions.Enqueue(ev);
        _wakeSignal.Set();
    }

    private void WaitWithDeadline(TimeSpan timeout)
    {
        int waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 0, 15);
        if (waitMs <= 0)
        {
            return;
        }

        _wakeSignal.Reset();
        if (_completions.IsEmpty)
        {
            _wakeSignal.Wait(waitMs);
        }
    }

    private sealed class ClientSession : IDisposable
    {
        private readonly Action<SocketAsyncEventArgs> _returnReceiveArgs;
        private int _needsRearm;
        private int _closed;

        public ClientSession(
            ConnectionId id,
            Socket socket,
            SocketAsyncEventArgs receiveArgs,
            Action<SocketAsyncEventArgs> returnReceiveArgs)
        {
            Id = id;
            Socket = socket;
            ReceiveArgs = receiveArgs;
            _returnReceiveArgs = returnReceiveArgs;
            ReceiveArgs.UserToken = this;
        }

        public ConnectionId Id { get; }

        public Socket Socket { get; }

        public SocketAsyncEventArgs ReceiveArgs { get; }

        public SlabSlotToken CurrentSlot { get; set; }

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MarkArmedForNextReceive() => Interlocked.Exchange(ref _needsRearm, 1) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConsumeNeedsRearm() => Interlocked.Exchange(ref _needsRearm, 0) == 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                Socket.Dispose();
            }
            finally
            {
                _returnReceiveArgs(ReceiveArgs);
            }
        }
    }
}
