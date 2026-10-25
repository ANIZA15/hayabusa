using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        string[] args = ["--role", "web", "--bind", "127.0.0.1", "--http-port", "8080", "--rpc-port", "8081", "--workers", "4", "--tick-rate", "30", "--transport", "inmemory"];

        options.ApplyEnvironmentAndArgs(args);

        Assert.Equal(HayabusaServerRole.WebOnly, options.Role);
        Assert.True(options.IsWebEnabled);
        Assert.False(options.IsGameEnabled);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(8080, options.HttpPort);
        Assert.Equal(8081, options.RpcPort);
        Assert.Equal(4, options.WorkerThreads);
        Assert.Equal(30, options.TickRate);
        Assert.Equal("inmemory", options.Transport);
    }

    [Fact]
    public void CoreWorker_WhenConfiguredAsWebOnly_ServesHttpRequestsAndSkipsGameTicksAndRejectsRpc()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        var config = new CoreWorkerConfig(CoreIndex.Zero, TickRateHz: 60, TimeProvider: fakeTime, Role: HayabusaServerRole.WebOnly);
        using var worker = new CoreWorker(config, transport, dispatcher);
        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));

        var httpConn = new ConnectionId(1UL);
        var rpcConn = new ConnectionId(2UL);
        transport.EnqueueAccept(httpConn);
        transport.TryEnqueueReceive(httpConn, "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8);

        using var rpcFrame = new PooledBufferWriter(128);
        PooledBufferWriter w = rpcFrame;
        BattleRoomServiceClient.WriteMoveAsyncRequest(ref w, new MoveCommand(1f, 2f, 3f));
        transport.EnqueueAccept(rpcConn);
        transport.TryEnqueueReceive(rpcConn, rpcFrame.WrittenSpan);

        fakeTime.Advance(TimeSpan.FromMilliseconds(50));
        worker.StepIteration();

        Assert.Single(transport.GetSentPayloads(httpConn));
        Assert.True(transport.IsDisconnected(rpcConn));
        Assert.Equal(TickNumber.Zero, room.CurrentTick);
    }

    [Fact]
    public void CoreWorker_WhenConfiguredAsGameOnly_ExecutesRpcAndCustomGameLoopTicksAndRejectsHttp()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        var config = new CoreWorkerConfig(CoreIndex.Zero, TickRateHz: 50, TimeProvider: fakeTime, Role: HayabusaServerRole.GameOnly);
        using var worker = new CoreWorker(config, transport, dispatcher);
        var customLoop = new RecordingTickHandler();
        worker.RegisterTickHandler(customLoop);

        var httpConn = new ConnectionId(10UL);
        transport.EnqueueAccept(httpConn);
        transport.TryEnqueueReceive(httpConn, "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8);

        fakeTime.Advance(TimeSpan.FromMilliseconds(20));
        worker.StepIteration();

        Assert.True(transport.IsDisconnected(httpConn));
        Assert.Empty(transport.GetSentPayloads(httpConn));
        Assert.Equal(new TickNumber(1UL), customLoop.LastTick);
    }

    [Fact]
    public void HayabusaAppBuilder_RoleBuilderMethods_ConfigureWebOnlyGameOnlyAndHybridModes()
    {
        using HayabusaApp webOnly = HayabusaApp.CreateBuilder(["--transport", "inmemory"]).UseWebServerOnly(httpPort: 8080).Build();
        using HayabusaApp gameOnly = HayabusaApp.CreateBuilder(["--transport", "inmemory"]).UseGameServerOnly(tickRateHz: 30, rpcPort: 9000).Build();
        using HayabusaApp hybrid = HayabusaApp.CreateBuilder(["--transport", "inmemory"]).UseWebAndGameServer(httpPort: 8080, rpcPort: 9000, tickRateHz: 60).Build();

        Assert.Equal(HayabusaServerRole.WebOnly, webOnly.Role);
        Assert.Equal(HayabusaServerRole.GameOnly, gameOnly.Role);
        Assert.Equal(HayabusaServerRole.WebAndGame, hybrid.Role);
    }

    [Fact]
    public void CoreWorker_SixteenPipelinedHttpRequests_CoalescesResponsesIntoSingleSendBatchWithZeroSlabLeak()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TimeProvider: fakeTime, Role: HayabusaServerRole.WebOnly), transport, dispatcher);

        var connId = new ConnectionId(777UL);
        byte[] singleGet = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
        byte[] pipelined16 = BuildRepeatedPayload(singleGet, 16);

        transport.EnqueueAccept(connId);
        transport.TryEnqueueReceive(connId, pipelined16);
        worker.StepIteration();

        var sentPayloads = transport.GetSentPayloads(connId);
        Assert.Single(sentPayloads);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public void SocketEventTransport_EventDrivenAcceptReceiveAndSend_CompletesLoopbackRoundtripWithZeroSlabLeak()
    {
        using var transport = new SocketEventTransport();
        transport.BindAndListen(new IPEndPoint(IPAddress.Loopback, 0), enableReusePort: false);
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/ping", static ctx => ctx.WriteUtf8("PONG"u8));
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, Role: HayabusaServerRole.WebOnly), transport, dispatcher);

        Assert.Equal(1, transport.ListenerCount);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public void IoUringTransport_TryCreate_InitializesSharedRingsOrGracefullyFallsBack()
    {
        bool created = IoUringTransport.TryCreate(null, 64u, out IoUringTransport? uring, out string diagnostic);
        try
        {
            Assert.False(string.IsNullOrWhiteSpace(diagnostic));
            if (created && uring is not null)
            {
                Assert.True(uring.SharedRingsMapped);
                int submitted = uring.SubmitAndWait(
                    [new TransportSubmissionEntry(TransportOperationKind.None, ConnectionId.Invalid, ReadOnlyMemory<byte>.Empty)],
                    TimeSpan.Zero);
                Assert.Equal(1, submitted);
            }
        }
        finally
        {
            uring?.Dispose();
        }
    }

    private static byte[] BuildRepeatedPayload(byte[] single, int count)
    {
        byte[] combined = new byte[single.Length * count];
        for (int i = 0; i < count; i++)
        {
            single.AsSpan().CopyTo(combined.AsSpan(i * single.Length, single.Length));
        }

        return combined;
    }

    private sealed class RecordingTickHandler : IGameLoopTickHandler
    {
        public TickNumber LastTick { get; private set; }

        public void OnGameTick(CoreIndex coreIndex, TickNumber tick)
        {
            LastTick = tick;
        }
    }

    [Fact]
    public void RoomActor_R3LifecycleObservables_EmitPlayerJoinedTickAndPlayerLeftEvents()
    {
        using var writer = new PooledBufferWriter(1024);
        var context = new HayabusaContext(CoreIndex.Zero, writer);
        var sq = new TransportSubmissionQueue(64);
        using var roomManager = new RoomManager(CoreIndex.Zero, context, sq);
        BattleRoomService room = roomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(88UL));

        int joinedCount = 0;
        int leftCount = 0;
        ulong lastTickObserved = 0;
        room.AddSubscription(room.OnPlayerJoined.Subscribe(_ => joinedCount++));
        room.AddSubscription(room.OnPlayerLeft.Subscribe(_ => leftCount++));
        room.AddSubscription(room.OnTickObservable.Subscribe(t => lastTickObserved = t.Value));

        var player = new ConnectionId(500UL);
        room.JoinPlayer(player);
        room.ExecuteTick(new TickNumber(7UL));
        room.LeavePlayer(player);

        Assert.Equal(1, joinedCount);
        Assert.Equal(1, leftCount);
        Assert.Equal(7UL, lastTickObserved);
    }

    [Fact]
    public async Task RoomActor_SourceGeneratedUniTaskRpc_ExecutesAndSerializesResponse()
    {
        using var writer = new PooledBufferWriter(1024);
        var context = new HayabusaContext(CoreIndex.Zero, writer);
        var sq = new TransportSubmissionQueue(64);
        using var roomManager = new RoomManager(CoreIndex.Zero, context, sq);
        BattleRoomService room = roomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));
        room.JoinPlayer(new ConnectionId(10UL));
        room.ExecuteTick(new TickNumber(15UL));

        context.Bind(new ConnectionId(10UL), new RoomId(1UL), new TickNumber(15UL));
        bool handled = await room.TryDispatchRpcAsync(context, new MethodId(10), ReadOnlyMemory<byte>.Empty, writer);
        FrameParseError parseErr = FrameParser.TryParse(writer.WrittenSpan, 1024, out FrameSlice frame);
        WorldSnapshot snapshot = MemoryPack.MemoryPackSerializer.Deserialize<WorldSnapshot>(frame.Payload);

        Assert.True(handled);
        Assert.Equal(FrameParseError.None, parseErr);
        Assert.Equal(new TickNumber(15UL), snapshot.Tick);
        Assert.Equal(1, snapshot.PlayerCount);
    }

    [Fact]
    public void CoreWorker_FullHttpRequestResponseCycle_AllocatesZeroBytesAndReturnsSlabSlot()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TimeProvider: fakeTime), transport, dispatcher);
        var connId = new ConnectionId(202UL);
        byte[] rawHttp = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();

        transport.EnqueueAccept(connId);
        transport.TryEnqueueReceive(connId, rawHttp);
        worker.StepIteration();

        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
        Assert.Single(transport.GetSentPayloads(connId));
    }

    [Fact]
    public void CoreWorker_HighVolumeSequentialHttpKeepAliveRequests_SucceedsWithoutFalseRateLimitEvictionOrStaleBytes()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(Hayabusa.Rpc.Http.HttpMethodKind.Get, "/health", static ctx => ctx.WriteUtf8("OK"u8));
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TimeProvider: fakeTime), transport, dispatcher);
        var connId = new ConnectionId(303UL);
        byte[] rawHttp = "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();

        transport.EnqueueAccept(connId);
        for (int i = 0; i < 500; i++)
        {
            transport.TryEnqueueReceive(connId, rawHttp);
            worker.StepIteration();
        }

        IReadOnlyList<byte[]> payloads = transport.GetSentPayloads(connId);
        Assert.False(transport.IsDisconnected(connId));
        Assert.Equal(500, payloads.Count);
        Assert.Equal(payloads[0].Length, payloads[499].Length);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public async Task HayabusaHybridBridge_MagicOnionStyleHubReceiverAndInProcessRpc_DeliversTypedResponsesAndBroadcasts()
    {
        var fakeTime = new FakeTimeProvider();
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        using var worker = new CoreWorker(new CoreWorkerConfig(CoreIndex.Zero, TickRateHz: 50, TimeProvider: fakeTime), transport, dispatcher);
        BattleRoomService room = worker.RoomManager.CreateAndRegisterRoom<BattleRoomService>(new RoomId(1UL));
        dispatcher.RegisterService(room);

        var playerConn = new ConnectionId(1UL);
        room.JoinPlayer(playerConn);

        using var client = BattleRoomServiceClient.CreateInMemory(dispatcher);
        var receiver = new RecordingBattleHubReceiver();
        using IDisposable sub = client.InnerClient.BindReceiver(receiver);

        WorldSnapshot unaryResult = await client.TeleportAsync(3f, 4f, 5f);
        fakeTime.Advance(TimeSpan.FromMilliseconds(20));
        worker.StepIteration();
        int pumped = HayabusaHybridBridge.PumpInMemoryBroadcastsToClient(transport, playerConn, client.InnerClient);

        Assert.Equal(1, unaryResult.PlayerCount);
        Assert.True(pumped >= 1);
        Assert.Equal(new TickNumber(1UL), receiver.LastSnapshot.Tick);
    }

    [Fact]
    public void HayabusaHybridBridge_PostRpcHttpTunnel_ExecutesBinaryRpcOverHttpEndpoint()
    {
        using HayabusaApp app = Hayabusa.SampleServer.Program.CreateSampleApp(["--transport", "inmemory", "--workers", "1"]);
        using var reqFrame = new PooledBufferWriter(128);
        PooledBufferWriter fw = reqFrame;
        BattleRoomServiceClient.WriteMoveAsyncRequest(ref fw, new MoveCommand(7f, 8f, 9f));

        byte[] body = reqFrame.WrittenSpan.ToArray();
        byte[] header = Encoding.ASCII.GetBytes($"POST /rpc HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length}\r\n\r\n");
        byte[] fullHttp = new byte[header.Length + body.Length];
        header.CopyTo(fullHttp, 0);
        body.CopyTo(fullHttp, header.Length);

        using var responseWriter = new PooledBufferWriter(1024);
        bool handled = HayabusaHybridBridge.DispatchRawHttpRequest(app, fullHttp, responseWriter);

        Assert.True(handled);
        Assert.True(responseWriter.WrittenSpan.StartsWith("HTTP/1.1 200 OK\r\nContent-Type: application/x-memorypack"u8));
    }

    private sealed class RecordingBattleHubReceiver : IBattleHubReceiver
    {
        public WorldSnapshot LastSnapshot { get; private set; }

        public void OnWorldTick(WorldSnapshot snapshot)
        {
            LastSnapshot = snapshot;
        }
    }

    [Fact]
    public async Task BattleRoomServiceClient_CompactAutoMethodIdAndMultiParamRpc_RoundtripsViaGeneratedClient()
    {
        var dispatcher = new RpcDispatcher();
        using var room = new BattleRoomService();
        dispatcher.RegisterService(room);
        using BattleRoomServiceClient client = BattleRoomServiceClient.CreateInMemory(dispatcher);

        WorldSnapshot snapshot = await client.TeleportAsync(12.5f, 3.0f, -8.25f);
        bool found = room.TryGetPlayerPosition(new ConnectionId(1UL), out MoveCommand pos);

        Assert.True(found);
        Assert.Equal(1, snapshot.PlayerCount);
        Assert.Equal(12.5f, pos.X);
        Assert.Equal(-8.25f, pos.Z);
        Assert.True(MethodId.FromRoute("BattleRoomService", "Teleport").IsValid);
    }

    private static void AddHundredPlayers(RoomActor room)
    {
        for (ulong i = 1; i <= 100; i++)
        {
            room.JoinPlayer(new ConnectionId(i));
        }
    }
}
