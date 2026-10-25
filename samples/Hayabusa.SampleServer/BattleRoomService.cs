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
/// MagicOnion-style StreamingHub receiver contract for server-to-client real-time push notifications.
/// The Source Generator emits <c>BattleHubReceiverBroadcaster</c> (for the server <see cref="RoomActor"/>)
/// and <c>BattleHubReceiverClientBinder</c> (for Unity / Blazor / .NET clients).
/// </summary>
[HayabusaHubReceiver]
public interface IBattleHubReceiver
{
    /// <summary>Invoked on every connected client when the room broadcasts a 60fps world tick snapshot.</summary>
    [Rpc(MethodId = 100)]
    void OnWorldTick(WorldSnapshot snapshot);
}

/// <summary>
/// MagicOnion-style shared contract interface for <see cref="BattleRoomService"/>.
/// Methods declared here are automatically discovered by the Hayabusa Source Generator even without repeating <c>[Rpc]</c> on the class.
/// </summary>
public interface IBattleRoomHub : IStreamingHub<IBattleRoomHub, IBattleHubReceiver>
{
    /// <summary>Updates the calling player's 3D coordinates.</summary>
    [Rpc(MethodId = 1)]
    ValueTask<WorldSnapshot> MoveAsync(MoveCommand command);
}

/// <summary>
/// Sample real-time battle room actor demonstrating both MagicOnion-style <see cref="IStreamingHub{TSelf, TReceiver}"/>,
/// <c>[HayabusaService]</c> source-generated RPC dispatch (<c>ValueTask&lt;T&gt;</c>, Cysharp <c>UniTask&lt;T&gt;</c>, and sync methods),
/// and deterministic 60fps <see cref="OnTick"/> state broadcasting without locks.
/// </summary>
[HayabusaService]
public sealed partial class BattleRoomService : RoomActor, IBattleRoomHub
{
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
    /// Compact RPC endpoint demonstrating auto-assigned <c>MethodId</c>, multiple parameters without a wrapper DTO,
    /// and a synchronous return value.
    /// </summary>
    [Rpc]
    public WorldSnapshot Teleport(float x, float y, float z)
    {
        UpdatePlayerPosition(Context.ConnectionId, new MoveCommand(x, y, z));
        return new WorldSnapshot(CurrentTick, PlayerCount);
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
        _ = this.BroadcastOnWorldTick(snapshot);
    }
}
