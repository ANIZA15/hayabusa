using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;

namespace Hayabusa.Runtime.Rooms;

/// <summary>
/// Manages all affinity-pinned <see cref="RoomActor"/> instances on a single worker thread (<see cref="CoreIndex"/>).
/// Because each <see cref="RoomManager"/> is exclusively accessed by its owning core thread,
/// room lookup, tick iteration, and lifecycle disposal require zero locks.
/// </summary>
public sealed class RoomManager : IDisposable
{
    private readonly CoreIndex _coreIndex;
    private readonly HayabusaContext _context;
    private readonly TransportSubmissionQueue _submissionQueue;
    private readonly Dictionary<RoomId, RoomActor> _roomsById = new(64);
    private readonly List<RoomActor> _activeRooms = new(64);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoomManager"/> class for a specific worker core.
    /// </summary>
    public RoomManager(CoreIndex coreIndex, HayabusaContext context, TransportSubmissionQueue submissionQueue)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(submissionQueue);

        _coreIndex = coreIndex;
        _context = context;
        _submissionQueue = submissionQueue;
    }

    /// <summary>Gets the worker core index owning this room manager.</summary>
    public CoreIndex CoreIndex => _coreIndex;

    /// <summary>Gets the number of active rooms currently registered on this core.</summary>
    public int ActiveCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _activeRooms.Count;
    }

    /// <summary>
    /// Creates and registers a <typeparamref name="TRoom"/> instance pinned to this core.
    /// Ownership of the room is transferred to this <see cref="RoomManager"/>.
    /// </summary>
    public TRoom CreateAndRegisterRoom<TRoom>(RoomId roomId)
        where TRoom : RoomActor, new()
    {
        var room = new TRoom();
        try
        {
            RegisterRoom(roomId, room);
            TRoom registered = room;
            room = null!;
            return registered;
        }
        finally
        {
            room?.Dispose();
        }
    }

    /// <summary>
    /// Registers a <see cref="RoomActor"/> pinned to this core.
    /// </summary>
    public void RegisterRoom(RoomId roomId, RoomActor room)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(room);
        if (!roomId.IsValid)
        {
            throw new ArgumentException("RoomId must be non-zero.", nameof(roomId));
        }

        room.RoomId = roomId;
        room.CoreIndex = _coreIndex;
        room.Context = _context;
        room.SubmissionQueue = _submissionQueue;

        if (_roomsById.TryAdd(roomId, room))
        {
            _activeRooms.Add(room);
        }
    }

    /// <summary>
    /// Attempts to retrieve an active room by its <paramref name="roomId"/> without throwing exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRoom(RoomId roomId, [MaybeNullWhen(false)] out RoomActor room) =>
        _roomsById.TryGetValue(roomId, out room);

    /// <summary>
    /// Removes and disposes a room, releasing all its R3 subscriptions and player lists.
    /// </summary>
    public bool DestroyRoom(RoomId roomId)
    {
        if (!_roomsById.Remove(roomId, out RoomActor? room))
        {
            return false;
        }

        _activeRooms.Remove(room);
        room.Dispose();
        return true;
    }

    /// <summary>
    /// Removes a disconnected player from all active rooms on this core.
    /// </summary>
    public void RemoveDisconnectedPlayer(ConnectionId connectionId)
    {
        for (int i = 0; i < _activeRooms.Count; i++)
        {
            _activeRooms[i].LeavePlayer(connectionId);
        }
    }

    /// <summary>
    /// Executes <see cref="RoomActor.ExecuteTick"/> on all active rooms belonging to this core.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TickAllRooms(TickNumber tick)
    {
        for (int i = 0; i < _activeRooms.Count; i++)
        {
            _activeRooms[i].ExecuteTick(tick);
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
        for (int i = 0; i < _activeRooms.Count; i++)
        {
            _activeRooms[i].Dispose();
        }

        _activeRooms.Clear();
        _roomsById.Clear();
    }
}
