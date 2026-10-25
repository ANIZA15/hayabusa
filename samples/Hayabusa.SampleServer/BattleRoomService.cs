using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using Hayabusa.Runtime.Rooms;
using MemoryPack;

namespace Hayabusa.SampleServer;

/// <summary>
/// Represents a player movement command serialized with zero-copy <c>MemoryPack</c>.
/// </summary>
[MemoryPackable]
public readonly partial record struct MoveCommand(float X, float Y, float Z);

/// <summary>
/// Represents a world state snapshot returned by RPC or broadcast on every Room tick.
/// </summary>
[MemoryPackable]
public readonly partial record struct WorldSnapshot(TickNumber Tick, int PlayerCount);

/// <summary>
/// Sample real-time battle room actor demonstrating both <c>[HayabusaService]</c> source-generated RPC dispatch
/// (<c>ValueTask&lt;T&gt;</c> and Cysharp <c>UniTask&lt;T&gt;</c>) and deterministic 60fps <see cref="OnTick"/> state broadcasting without locks.
/// </summary>
[HayabusaService]
public sealed partial class BattleRoomService : RoomActor
{
    private static readonly MethodId SnapshotBroadcastMethodId = new(100);
    private readonly Dictionary<ConnectionId, MoveCommand> _playerPositions = new();

    /// <summary>Gets the last known position of a connected player.</summary>
    public bool TryGetPlayerPosition(ConnectionId connectionId, out MoveCommand position) =>
        _playerPositions.TryGetValue(connectionId, out position);

    /// <summary>
    /// Updates the calling player's 3D coordinates on the room's pinned worker thread with zero locks.
    /// </summary>
    [Rpc(MethodId = 1)]
    public ValueTask<WorldSnapshot> MoveAsync(MoveCommand command)
    {
        UpdatePlayerPosition(Context.ConnectionId, command);
        return new ValueTask<WorldSnapshot>(new WorldSnapshot(CurrentTick, PlayerCount));
    }

    /// <summary>
    /// Returns the current room snapshot using Cysharp <c>UniTask&lt;T&gt;</c> with zero heap allocation.
    /// </summary>
    [Rpc(MethodId = 10)]
    public UniTask<WorldSnapshot> GetWorldSnapshotUniTaskAsync()
    {
        return UniTask.FromResult(new WorldSnapshot(CurrentTick, PlayerCount));
    }

    /// <summary>
    /// Health-check HTTP endpoint generated via <c>[Get]</c> attribute routing.
    /// </summary>
    [Get("/room/status")]
    public void GetRoomStatus()
    {
        _ = PlayerCount;
    }

    /// <inheritdoc />
    protected override void OnTick(TickNumber tick)
    {
        BroadcastWorldState(tick);
    }

    private void UpdatePlayerPosition(ConnectionId connectionId, MoveCommand command)
    {
        if (connectionId.IsValid)
        {
            JoinPlayer(connectionId);
            _playerPositions[connectionId] = command;
        }
    }

    private void BroadcastWorldState(TickNumber tick)
    {
        var snapshot = new WorldSnapshot(tick, PlayerCount);
        _ = BroadcastSnapshot(SnapshotBroadcastMethodId, in snapshot);
    }
}
