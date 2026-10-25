using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using R3;

namespace Hayabusa.Runtime.Rooms;

/// <summary>
/// Base class for affinity-pinned, lock-free real-time game rooms (Actors).
/// Every <see cref="RoomActor"/> is bound to a single <see cref="CoreIndex"/> thread; its state and <see cref="OnTick"/>
/// are only ever accessed from that dedicated core thread, eliminating all locks.
/// Uses Cysharp <c>R3</c> <see cref="DisposableBag"/> for zero-leak reactive subscription lifecycle management.
/// </summary>
public abstract class RoomActor : IHayabusaRpcService, IDisposable
{
    private readonly HashSet<ConnectionId> _joinedPlayers = new();
    private readonly List<ConnectionId> _playerList = new();
    private readonly PooledBufferWriter _broadcastBuffer = new(64 * 1024);
    private DisposableBag _subscriptions;
    private bool _disposed;

    /// <summary>Gets the unique identifier of this room.</summary>
    public RoomId RoomId { get; internal set; }

    /// <summary>Gets the worker core index to which this room is pinned.</summary>
    public CoreIndex CoreIndex { get; internal set; }

    /// <summary>Gets the current tick number on this room's worker loop.</summary>
    public TickNumber CurrentTick { get; protected internal set; }

    /// <summary>Gets the current RPC execution context during an RPC call.</summary>
    public HayabusaContext Context { get; protected internal set; } = null!;

    /// <summary>Gets the outbound transport submission queue for this room's worker core.</summary>
    internal TransportSubmissionQueue? SubmissionQueue { get; set; }

    /// <summary>Gets the number of players currently joined in this room.</summary>
    public int PlayerCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _playerList.Count;
    }

    /// <summary>Gets the read-only list of joined player connection IDs.</summary>
    public IReadOnlyList<ConnectionId> Players => _playerList;

    /// <summary>
    /// Adds an <see cref="IDisposable"/> (such as an R3 observable subscription) to this room's <see cref="DisposableBag"/>
    /// so it is automatically disposed when the room is destroyed.
    /// </summary>
    public void AddSubscription(IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _subscriptions.Add(subscription);
    }

    /// <summary>
    /// Adds a player connection to this room. Must be called on the owning <see cref="CoreIndex"/> thread.
    /// </summary>
    public bool JoinPlayer(ConnectionId connectionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!connectionId.IsValid || !_joinedPlayers.Add(connectionId))
        {
            return false;
        }

        _playerList.Add(connectionId);
        return true;
    }

    /// <summary>
    /// Removes a player connection from this room. Must be called on the owning <see cref="CoreIndex"/> thread.
    /// </summary>
    public bool LeavePlayer(ConnectionId connectionId)
    {
        if (!_joinedPlayers.Remove(connectionId))
        {
            return false;
        }

        _playerList.Remove(connectionId);
        return true;
    }

    /// <summary>
    /// Checks whether <paramref name="connectionId"/> is an authorized member of this room (Cross-Tenant Spoofing defense).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPlayerAuthorized(ConnectionId connectionId) => _joinedPlayers.Contains(connectionId);

    /// <summary>
    /// Serializes <paramref name="snapshot"/> once using <c>MemoryPack</c> and enqueues a zero-copy shared reference
    /// to every joined player's submission queue with 0 B heap allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int BroadcastSnapshot<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSnapshot>(
        MethodId methodId,
        in TSnapshot snapshot)
    {
        if (_playerList.Count == 0 || SubmissionQueue is null)
        {
            return 0;
        }

        _broadcastBuffer.Clear();
        PooledBufferWriter writer = _broadcastBuffer;
        FrameParser.WriteMemoryPackFrame(ref writer, methodId, RpcFrameFlags.Broadcast, in snapshot);
        ReadOnlyMemory<byte> sharedFrame = writer.WrittenMemory;

        int sentCount = 0;
        for (int i = 0; i < _playerList.Count; i++)
        {
            if (SubmissionQueue.TryEnqueueSend(_playerList[i], sharedFrame))
            {
                sentCount++;
            }
        }

        return sentCount;
    }

    /// <summary>
    /// Invoked deterministically at the configured tick frequency (e.g. 60Hz / 30Hz) on the owning worker core thread.
    /// </summary>
    /// <param name="tick">The current monotonically increasing tick number.</param>
    protected virtual void OnTick(TickNumber tick)
    {
    }

    /// <summary>Internal entry point invoked by the worker loop scheduler during Phase 2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteTick(TickNumber tick)
    {
        CurrentTick = tick;
        OnTick(tick);
    }

    /// <inheritdoc />
    public virtual ValueTask<bool> TryDispatchRpcAsync(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlyMemory<byte> payload,
        IBufferWriter<byte> responseWriter)
    {
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public virtual bool TryDispatchHttp(
        HayabusaContext context,
        in HttpRequestView request,
        IBufferWriter<byte> responseWriter)
    {
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases room subscriptions, player lists, and reusable broadcast buffers.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _subscriptions.Dispose();
            _joinedPlayers.Clear();
            _playerList.Clear();
            _broadcastBuffer.Dispose();
        }
    }
}
