using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Rooms;
using Hayabusa.Runtime.Security;
using Hayabusa.Runtime.Threading;
using Hayabusa.Runtime.Timing;
using MemoryPack;

namespace Hayabusa.Runtime.Engine;

/// <summary>
/// Configuration parameters for a single <see cref="CoreWorker"/> instance.
/// Grouped into a <c>readonly record struct</c> to keep constructor parameter counts below 4.
/// </summary>
/// <param name="CoreIndex">Zero-based worker core index.</param>
/// <param name="TickRateHz">Target room tick frequency in Hz.</param>
/// <param name="MaxFrameSize">Maximum allowed RPC frame payload size in bytes.</param>
/// <param name="TimeProvider">Time provider for deterministic scheduling and timeout sweeps.</param>
public readonly record struct CoreWorkerConfig(
    CoreIndex CoreIndex,
    int TickRateHz = 60,
    int MaxFrameSize = RpcHeader.DefaultMaxFrameSize,
    TimeProvider? TimeProvider = null);

/// <summary>
/// Implements the Thread-per-Core 4-phase event loop combining completion-based I/O,
/// cross-core lock-free MPSC mailboxes, LogicLooper-style room ticks, and TimingWheel timeout sweeps.
/// </summary>
public sealed class CoreWorker : IDisposable
{
    private readonly CoreIndex _coreIndex;
    private readonly ITransportBackend _transport;
    private readonly RpcDispatcher _dispatcher;
    private readonly LogicTickScheduler _tickScheduler;
    private readonly TimingWheel _timingWheel;
    private readonly ConnectionRateLimiter _rateLimiter;
    private readonly TransportSubmissionQueue _submissionQueue;
    private readonly PooledBufferWriter _responseWriter;
    private readonly HayabusaContext _context;
    private readonly RoomManager _roomManager;
    private readonly MpscMailbox<CrossCoreRpcMessage> _mailbox;
    private readonly Dictionary<ConnectionId, ContiguousReceiveBuffer> _receiveBuffers = new(128);
    private readonly TransportCompletionEvent[] _cqeScratch = new TransportCompletionEvent[256];
    private readonly ConnectionId[] _expiredScratch = new ConnectionId[64];
    private readonly TickNumber[] _dueTicksScratch = new TickNumber[8];
    private readonly int _maxFrameSize;
    private SingleThreadSynchronizationContext? _syncContext;
    private int _lastObservedThreadId;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoreWorker"/> class.
    /// </summary>
    public CoreWorker(
        CoreWorkerConfig config,
        ITransportBackend transport,
        RpcDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _coreIndex = config.CoreIndex;
        _transport = transport;
        _dispatcher = dispatcher;
        _maxFrameSize = config.MaxFrameSize > 0 ? config.MaxFrameSize : RpcHeader.DefaultMaxFrameSize;

        TimeProvider timeProvider = config.TimeProvider ?? TimeProvider.System;
        _tickScheduler = new LogicTickScheduler(config.TickRateHz > 0 ? config.TickRateHz : 60, timeProvider);
        _timingWheel = new TimingWheel(timeProvider);
        _rateLimiter = new ConnectionRateLimiter(timeProvider);
        _submissionQueue = new TransportSubmissionQueue(1024);
        _responseWriter = new PooledBufferWriter(64 * 1024);
        _context = new HayabusaContext(_coreIndex, _responseWriter);
        _roomManager = new RoomManager(_coreIndex, _context, _submissionQueue);
        _mailbox = new MpscMailbox<CrossCoreRpcMessage>(1024);
    }

    /// <summary>Gets the core index of this worker.</summary>
    public CoreIndex CoreIndex => _coreIndex;

    /// <summary>Gets the room manager pinned to this worker core.</summary>
    public RoomManager RoomManager => _roomManager;

    /// <summary>Gets the tick scheduler of this worker core.</summary>
    public LogicTickScheduler TickScheduler => _tickScheduler;

    /// <summary>Gets the timing wheel of this worker core.</summary>
    public TimingWheel TimingWheel => _timingWheel;

    /// <summary>Gets the connection rate limiter of this worker core.</summary>
    public ConnectionRateLimiter RateLimiter => _rateLimiter;

    /// <summary>Gets the lock-free cross-core mailbox of this worker core.</summary>
    public MpscMailbox<CrossCoreRpcMessage> Mailbox => _mailbox;

    /// <summary>Gets the managed thread ID observed during the most recent execution on this worker.</summary>
    public int LastObservedThreadId => _lastObservedThreadId;

    /// <summary>Gets the number of active receive buffers tracked on this worker.</summary>
    public int ActiveConnectionCount => _receiveBuffers.Count;

    /// <summary>
    /// Executes a single deterministic iteration of the 4-phase worker loop:
    /// Phase 1 (Drain I/O &amp; Mailboxes) -&gt; Phase 2 (Room Ticks) -&gt; Phase 3 (Sweep Timeouts) -&gt; Phase 4 (Batch Flush &amp; Wait).
    /// </summary>
    public void StepIteration(bool allowWaitForDeadline = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureThreadContextInstalled();

        // Phase 1: Drain I/O Completions, Continuations & Cross-Core Mailboxes
        DrainPhase1();

        // Phase 2: Execute Scheduled Room Ticks (LogicLooper style)
        ExecutePhase2RoomTicks();

        // Phase 3: Sweep Timeouts & Evict Dead/Slowloris Connections
        SweepPhase3Timeouts();

        // Phase 4: Batch Flush / Submit I/O with next Tick Deadline
        FlushPhase4Submissions(allowWaitForDeadline);
    }

    /// <summary>
    /// Manually steps a single room tick for deterministic unit testing.
    /// </summary>
    public void StepManualTick(TickNumber tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureThreadContextInstalled();
        _roomManager.TickAllRooms(tick);
        FlushPhase4Submissions(allowWaitForDeadline: false);
    }

    /// <summary>
    /// Runs the dedicated worker loop on a pinned OS thread until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public void RunBlockingLoop(CancellationToken cancellationToken)
    {
        EnsureThreadContextInstalled();
        _tickScheduler.ResetSchedule();

        while (!cancellationToken.IsCancellationRequested)
        {
            StepIteration(allowWaitForDeadline: true);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _roomManager.Dispose();
        _responseWriter.Dispose();
        _receiveBuffers.Clear();
    }

    private void EnsureThreadContextInstalled()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        _lastObservedThreadId = currentThreadId;
        if (_syncContext is null || !_syncContext.IsOnOwnerThread)
        {
            _syncContext = new SingleThreadSynchronizationContext(_coreIndex, currentThreadId);
            SynchronizationContext.SetSynchronizationContext(_syncContext);
        }
    }

    private void DrainPhase1()
    {
        int completedCount = _transport.DrainCompletions(_cqeScratch);
        for (int i = 0; i < completedCount; i++)
        {
            ProcessCompletionEvent(in _cqeScratch[i]);
        }

        DrainCrossCoreMailbox();
        _syncContext?.DrainContinuations();
    }

    private void ProcessCompletionEvent(in TransportCompletionEvent ev)
    {
        switch (ev.Operation)
        {
            case TransportOperationKind.Accept:
                HandleAccept(ev.ConnectionId);
                break;
            case TransportOperationKind.Receive:
                HandleReceive(in ev);
                break;
            case TransportOperationKind.Disconnect:
                EvictConnection(ev.ConnectionId, enqueueTransportDisconnect: false);
                break;
            case TransportOperationKind.Send:
            case TransportOperationKind.None:
            default:
                ReturnSlabIfPresent(ev.SlabToken);
                break;
        }
    }

    private void HandleAccept(ConnectionId connectionId)
    {
        if (!_receiveBuffers.ContainsKey(connectionId))
        {
            _receiveBuffers[connectionId] = new ContiguousReceiveBuffer(_maxFrameSize * 2);
        }

        _timingWheel.TouchCompleteFrame(connectionId);
    }

    private void HandleReceive(in TransportCompletionEvent ev)
    {
        try
        {
            if (ev.BytesTransferred <= 0)
            {
                EvictConnection(ev.ConnectionId, enqueueTransportDisconnect: true);
                return;
            }

            ReadOnlySpan<byte> incoming = _transport.SlabPool
                .GetSlotSpan(ev.SlabSlotIndex, ev.SlabGeneration)
                .Slice(0, ev.BytesTransferred);

            ProcessIncomingBytes(ev.ConnectionId, incoming);
        }
        finally
        {
            ReturnSlabIfPresent(ev.SlabToken);
        }
    }

    private void ProcessIncomingBytes(ConnectionId connectionId, scoped ReadOnlySpan<byte> incoming)
    {
        if (!_receiveBuffers.TryGetValue(connectionId, out ContiguousReceiveBuffer? buffer))
        {
            buffer = new ContiguousReceiveBuffer(_maxFrameSize * 2);
            _receiveBuffers[connectionId] = buffer;
        }

        if (!buffer.TryAppend(incoming))
        {
            EvictConnection(connectionId, enqueueTransportDisconnect: true);
            return;
        }

        DrainBufferedFrames(connectionId, buffer);
    }

    private void DrainBufferedFrames(ConnectionId connectionId, ContiguousReceiveBuffer buffer)
    {
        while (buffer.ReadableBytes > 0)
        {
            ReadOnlySpan<byte> readable = buffer.ReadableSpan;
            if (LooksLikeHttpRequest(readable))
            {
                if (!TryProcessHttpBuffer(connectionId, buffer))
                {
                    return;
                }

                continue;
            }

            if (!TryProcessRpcBuffer(connectionId, buffer))
            {
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool LooksLikeHttpRequest(scoped ReadOnlySpan<byte> span)
    {
        if (span.Length >= 2 && span[0] == RpcHeader.MagicByte0 && span[1] == RpcHeader.MagicByte1)
        {
            return false;
        }

        return span[0] is (byte)'G' or (byte)'P' or (byte)'D' or (byte)'H';
    }

    private bool TryProcessHttpBuffer(ConnectionId connectionId, ContiguousReceiveBuffer buffer)
    {
        HttpParseError error = HttpParser.TryParseRequest(buffer.ReadableSpan, out HttpRequestView req);
        if (error == HttpParseError.IncompleteData)
        {
            _timingWheel.RecordPartialFrameDrip(connectionId, 1);
            return false;
        }

        if (error != HttpParseError.None)
        {
            SendBadRequestAndDisconnect(connectionId);
            return false;
        }

        if (!_rateLimiter.TryAcquire(connectionId))
        {
            EvictConnection(connectionId, enqueueTransportDisconnect: true);
            return false;
        }

        ExecuteParsedHttpRequest(connectionId, in req);
        buffer.Consume(req.ConsumedBytes);
        _rateLimiter.ReleaseInFlight(connectionId);
        _timingWheel.TouchCompleteFrame(connectionId);
        return true;
    }

    private void ExecuteParsedHttpRequest(ConnectionId connectionId, in HttpRequestView req)
    {
        _context.Bind(connectionId, RoomId.Invalid, _tickScheduler.CurrentTick);
        _dispatcher.DispatchHttp(_context, in req, _responseWriter);
        if (_responseWriter.WrittenCount > 0)
        {
            _submissionQueue.TryEnqueueSend(connectionId, _responseWriter.WrittenMemory.ToArray());
        }
    }

    private void SendBadRequestAndDisconnect(ConnectionId connectionId)
    {
        _responseWriter.Clear();
        PooledBufferWriter writer = _responseWriter;
        HttpResponseWriter.WriteBadRequest(ref writer);
        _submissionQueue.TryEnqueueSend(connectionId, writer.WrittenMemory.ToArray());
        EvictConnection(connectionId, enqueueTransportDisconnect: true);
    }

    private bool TryProcessRpcBuffer(ConnectionId connectionId, ContiguousReceiveBuffer buffer)
    {
        FrameParseError error = FrameParser.TryParse(buffer.ReadableSpan, _maxFrameSize, out FrameSlice frame);
        if (error == FrameParseError.IncompleteData)
        {
            _timingWheel.RecordPartialFrameDrip(connectionId, 1);
            return false;
        }

        if (error != FrameParseError.None || !_rateLimiter.TryAcquire(connectionId))
        {
            EvictConnection(connectionId, enqueueTransportDisconnect: true);
            return false;
        }

        int consumed = frame.ConsumedBytes;
        ExecuteParsedRpcFrame(connectionId, frame.Header.MethodId, buffer.ReadableSpan.Slice(RpcHeader.Size, frame.Header.PayloadLength).ToArray());
        buffer.Consume(consumed);
        _rateLimiter.ReleaseInFlight(connectionId);
        _timingWheel.TouchCompleteFrame(connectionId);
        return true;
    }

    private void ExecuteParsedRpcFrame(ConnectionId connectionId, MethodId methodId, ReadOnlyMemory<byte> payload)
    {
        try
        {
            _context.Bind(connectionId, RoomId.Invalid, _tickScheduler.CurrentTick);
            ValueTask<bool> dispatchTask = _dispatcher.DispatchAsync(_context, methodId, payload, _responseWriter);
            if (dispatchTask.IsCompleted)
            {
                _ = dispatchTask.GetAwaiter().GetResult();
                EnqueueBufferedResponseIfAny(connectionId);
                return;
            }

            AwaitRpcContinuation(connectionId, dispatchTask);
        }
        catch (MemoryPackSerializationException)
        {
            EvictConnection(connectionId, enqueueTransportDisconnect: true);
        }
    }

    private async void AwaitRpcContinuation(ConnectionId connectionId, ValueTask<bool> dispatchTask)
    {
        try
        {
            await dispatchTask;
            EnqueueBufferedResponseIfAny(connectionId);
        }
        catch (MemoryPackSerializationException)
        {
            EvictConnection(connectionId, enqueueTransportDisconnect: true);
        }
    }

    private void EnqueueBufferedResponseIfAny(ConnectionId connectionId)
    {
        if (_responseWriter.WrittenCount > 0)
        {
            _submissionQueue.TryEnqueueSend(connectionId, _responseWriter.WrittenMemory.ToArray());
        }
    }

    private void DrainCrossCoreMailbox()
    {
        while (_mailbox.TryDequeue(out CrossCoreRpcMessage msg))
        {
            DispatchRoomMessageOnThisCore(in msg);
        }
    }

    private void DispatchRoomMessageOnThisCore(in CrossCoreRpcMessage msg)
    {
        if (!_roomManager.TryGetRoom(msg.RoomId, out RoomActor? room))
        {
            return;
        }

        if (!room.IsPlayerAuthorized(msg.ConnectionId))
        {
            return;
        }

        FrameParseError error = FrameParser.TryParse(msg.PayloadFrame.Span, _maxFrameSize, out FrameSlice slice);
        if (error != FrameParseError.None)
        {
            return;
        }

        _context.Bind(msg.ConnectionId, msg.RoomId, _tickScheduler.CurrentTick);
        ReadOnlyMemory<byte> bodySlice = msg.PayloadFrame.Slice(RpcHeader.Size, slice.Header.PayloadLength);
        ValueTask<bool> task = room.TryDispatchRpcAsync(_context, slice.Header.MethodId, bodySlice, _responseWriter);
        if (task.IsCompleted)
        {
            _ = task.GetAwaiter().GetResult();
            EnqueueBufferedResponseIfAny(msg.ConnectionId);
        }
    }

    private void ExecutePhase2RoomTicks()
    {
        int dueCount = _tickScheduler.CollectDueTicks(_dueTicksScratch);
        for (int i = 0; i < dueCount; i++)
        {
            _roomManager.TickAllRooms(_dueTicksScratch[i]);
        }
    }

    private void SweepPhase3Timeouts()
    {
        int expiredCount = _timingWheel.SweepExpired(_expiredScratch);
        for (int i = 0; i < expiredCount; i++)
        {
            EvictConnection(_expiredScratch[i], enqueueTransportDisconnect: true);
        }
    }

    private void FlushPhase4Submissions(bool allowWaitForDeadline)
    {
        TimeSpan waitDeadline = allowWaitForDeadline ? _tickScheduler.GetTimeUntilNextTick() : TimeSpan.Zero;
        if (_submissionQueue.Count > 0 || allowWaitForDeadline)
        {
            _transport.SubmitAndWait(_submissionQueue.PendingEntries, waitDeadline);
            _submissionQueue.Clear();
        }
    }

    private void EvictConnection(ConnectionId connectionId, bool enqueueTransportDisconnect)
    {
        _receiveBuffers.Remove(connectionId);
        _timingWheel.Remove(connectionId);
        _rateLimiter.Remove(connectionId);
        _roomManager.RemoveDisconnectedPlayer(connectionId);

        if (enqueueTransportDisconnect)
        {
            _submissionQueue.TryEnqueueDisconnect(connectionId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnSlabIfPresent(SlabSlotToken token)
    {
        if (token.HasSlot)
        {
            _transport.SlabPool.ReturnSlot(token.SlotIndex, token.Generation);
        }
    }
}
