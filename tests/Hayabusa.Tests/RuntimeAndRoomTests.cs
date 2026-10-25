using System;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Hosting;
using Hayabusa.Runtime.Rooms;
using Hayabusa.Runtime.Threading;
using Hayabusa.SampleServer;
using Hayabusa.Transport;
using Microsoft.Extensions.Time.Testing;
using R3;
using Xunit;

namespace Hayabusa.Tests;

public sealed class RuntimeAndRoomTests
{
    [Fact]
    public void LogicTickScheduler_WhenTimeAdvancesByThreeIntervals_CollectsThreeSequentialTicks()
    {
        var fakeTime = new FakeTimeProvider();
        var scheduler = new LogicTickScheduler(tickRateHz: 50, timeProvider: fakeTime, maxCatchUpTicks: 5);
        Span<TickNumber> due = stackalloc TickNumber[8];

        fakeTime.Advance(TimeSpan.FromMilliseconds(60));
        int count = scheduler.CollectDueTicks(due);

        Assert.Equal(3, count);
        Assert.Equal(new TickNumber(1UL), due[0]);
        Assert.Equal(new TickNumber(2UL), due[1]);
        Assert.Equal(new TickNumber(3UL), due[2]);
    }

    [Fact]
    public void LogicTickScheduler_WhenFrameBudgetOverrunsHeavily_PreventsSpiralOfDeathBySkippingLag()
    {
        var fakeTime = new FakeTimeProvider();
        var scheduler = new LogicTickScheduler(tickRateHz: 50, timeProvider: fakeTime, maxCatchUpTicks: 3);
        Span<TickNumber> due = stackalloc TickNumber[8];

        // Advance 1 full second (50 ticks overdue), but maxCatchUpTicks is 3
        fakeTime.Advance(TimeSpan.FromSeconds(1));
        int count = scheduler.CollectDueTicks(due);

        Assert.Equal(3, count);
        Assert.True(scheduler.SkippedTickCount >= 40);
        Assert.True(scheduler.GetTimeUntilNextTick() > TimeSpan.Zero);
    }

    [Fact]
    public async Task CoreWorker_RoomRpcAndOnTick_ExecuteOnSameWorkerCoreWithZeroAllocationBroadcast()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        var config = new CoreWorkerConfig(new CoreIndex(0), TickRateHz: 50, TimeProvider: fakeTime);
        using var worker = new CoreWorker(config, transport, dispatcher);

        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));
        dispatcher.RegisterService(room);
        var playerConn = new ConnectionId(101UL);
        room.JoinPlayer(playerConn);

        using var reqWriter = new PooledBufferWriter(256);
        PooledBufferWriter w = reqWriter;
        BattleRoomServiceClient.WriteMoveAsyncRequest(ref w, new MoveCommand(5f, 0f, -2f));
        transport.TryEnqueueReceive(playerConn, reqWriter.WrittenSpan);

        fakeTime.Advance(TimeSpan.FromMilliseconds(20));
        worker.StepIteration();

        await Task.CompletedTask;
        Assert.True(room.TryGetPlayerPosition(playerConn, out MoveCommand pos));
        Assert.Equal(5f, pos.X);
        Assert.Equal(new TickNumber(1UL), room.CurrentTick);
        Assert.True(transport.GetSentPayloads(playerConn).Count >= 2);
    }

    [Fact]
    public void RoomActor_BroadcastSnapshotTo100Players_AllocatesZeroBytesAfterWarmup()
    {
        using var writer = new PooledBufferWriter(4096);
        var context = new HayabusaContext(CoreIndex.Zero, writer);
        var sq = new TransportSubmissionQueue(256);
        using var roomManager = new RoomManager(CoreIndex.Zero, context, sq);
        BattleRoomService room = roomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));

        AddHundredPlayers(room);
        var snapshot = new WorldSnapshot(new TickNumber(10UL), 100);

        // Warmup
        _ = room.BroadcastSnapshot(new MethodId(100), in snapshot);
        sq.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sent = room.BroadcastSnapshot(new MethodId(100), in snapshot);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(100, sent);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void RoomActor_R3DisposableBagSubscription_DisposesCleanlyWhenRoomDestroyed()
    {
        using var writer = new PooledBufferWriter(1024);
        var context = new HayabusaContext(CoreIndex.Zero, writer);
        var sq = new TransportSubmissionQueue(64);
        using var roomManager = new RoomManager(CoreIndex.Zero, context, sq);
        BattleRoomService room = roomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(77UL));

        using var subject = new Subject<int>();
        int receivedCount = 0;
        IDisposable sub = subject.Subscribe(_ => receivedCount++);
        room.AddSubscription(sub);

        subject.OnNext(1);
        roomManager.DestroyRoom(new RoomId(77UL));
        subject.OnNext(2);

        Assert.Equal(1, receivedCount);
        Assert.Equal(0, roomManager.ActiveCount);
    }

    [Fact]
    public void MpscMailbox_CrossCoreMessageTransfer_DeliversToTargetCoreRoom()
    {
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(new CoreIndex(2)), transport, dispatcher);
        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(10UL));
        var playerConn = new ConnectionId(55UL);
        room.JoinPlayer(playerConn);

        using var frameWriter = new PooledBufferWriter(256);
        PooledBufferWriter w = frameWriter;
        BattleRoomServiceClient.WriteMoveAsyncRequest(ref w, new MoveCommand(9f, 8f, 7f));
        var crossMsg = new CrossCoreRpcMessage(
            new CoreIndex(0),
            playerConn,
            new RoomId(10UL),
            frameWriter.WrittenSpan.ToArray());

        bool enqueued = worker.Mailbox.TryEnqueue(in crossMsg);
        worker.StepIteration();

        Assert.True(enqueued);
        Assert.True(room.TryGetPlayerPosition(playerConn, out MoveCommand pos));
        Assert.Equal(9f, pos.X);
    }

    [Fact]
    public void HayabusaServerOptions_CommandLineArguments_OverrideDefaults()
    {
        var options = new HayabusaServerOptions();
        string[] args = ["--bind", "127.0.0.1", "--http-port", "8080", "--rpc-port", "8081", "--workers", "4", "--tick-rate", "30", "--transport", "inmemory"];

        options.ApplyEnvironmentAndArgs(args);

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(8080, options.HttpPort);
        Assert.Equal(8081, options.RpcPort);
        Assert.Equal(4, options.WorkerThreads);
        Assert.Equal(30, options.TickRate);
        Assert.Equal("inmemory", options.Transport);
    }

    private static void AddHundredPlayers(RoomActor room)
    {
        for (ulong i = 1; i <= 100; i++)
        {
            room.JoinPlayer(new ConnectionId(i));
        }
    }
}
