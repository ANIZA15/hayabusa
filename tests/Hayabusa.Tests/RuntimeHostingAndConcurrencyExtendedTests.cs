using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Hosting;
using Hayabusa.Runtime.Rooms;
using Hayabusa.Runtime.Security;
using Hayabusa.Runtime.Threading;
using Hayabusa.Runtime.Timing;
using Hayabusa.SampleServer;
using Hayabusa.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Hayabusa.Tests;

public sealed class RuntimeHostingAndConcurrencyExtendedTests
{
    [Fact]
    public void LogicTickScheduler_ValidationPropertiesAndZeroDueTicks_BehaveDeterministically()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogicTickScheduler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogicTickScheduler(60, maxCatchUpTicks: 0));

        var fakeTime = new FakeTimeProvider();
        var scheduler = new LogicTickScheduler(tickRateHz: 50, timeProvider: fakeTime, maxCatchUpTicks: 4);
        Span<TickNumber> due = stackalloc TickNumber[4];

        Assert.Equal(50, scheduler.TickRateHz);
        Assert.Equal(TimeSpan.FromMilliseconds(20), scheduler.TickInterval);
        Assert.Equal(4, scheduler.MaxCatchUpTicks);
        Assert.Equal(TickNumber.Zero, scheduler.CurrentTick);
        Assert.Equal(0, scheduler.CollectDueTicks(due));
        Assert.Equal(0, scheduler.CollectDueTicks(Span<TickNumber>.Empty));
        Assert.True(scheduler.GetTimeUntilNextTick() > TimeSpan.Zero);

        fakeTime.Advance(TimeSpan.FromMilliseconds(25));
        Assert.Equal(TimeSpan.Zero, scheduler.GetTimeUntilNextTick());
        scheduler.ResetSchedule();
        Assert.True(scheduler.GetTimeUntilNextTick() > TimeSpan.Zero);
    }

    [Fact]
    public void TimingWheel_IdleTimeoutAndPartialFrameRecovery_TrackAndEvictCorrectly()
    {
        var fakeTime = new FakeTimeProvider();
        var wheel = new TimingWheel(
            fakeTime,
            idleTimeout: TimeSpan.FromSeconds(10),
            frameReadTimeout: TimeSpan.FromSeconds(3));

        var idleConn = new ConnectionId(1UL);
        var recoveringConn = new ConnectionId(2UL);
        wheel.TouchCompleteFrame(idleConn);
        wheel.RecordPartialFrameDrip(recoveringConn, 2);
        wheel.RecordPartialFrameDrip(recoveringConn, 2);

        // Recovering connection completes its frame before the 3s frameReadTimeout
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        wheel.TouchCompleteFrame(recoveringConn);

        // Advance another 2s (total 4s): recoveringConn should NOT expire on frameReadTimeout because it completed
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        Span<ConnectionId> expired = stackalloc ConnectionId[4];
        Assert.Equal(0, wheel.SweepExpired(expired));

        // Advance 7s (idleConn is at 11s > 10s idleTimeout; recoveringConn is at 9s < 10s)
        fakeTime.Advance(TimeSpan.FromSeconds(7));
        int count = wheel.SweepExpired(expired);

        Assert.Equal(1, count);
        Assert.Equal(idleConn, expired[0]);
        Assert.Equal(1, wheel.TrackedCount);
        Assert.True(wheel.Remove(recoveringConn));
        Assert.Equal(0, wheel.SweepExpired(expired));
    }

    [Fact]
    public void ConnectionRateLimiter_TokenExhaustionRefillAndInFlightBackpressure_EnforceLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionRateLimiter(maxBurstTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionRateLimiter(refillTokensPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionRateLimiter(maxInFlightRequests: 0));

        var fakeTime = new FakeTimeProvider();
        var limiter = new ConnectionRateLimiter(
            fakeTime,
            maxBurstTokens: 2,
            refillTokensPerSecond: 2.0,
            maxInFlightRequests: 2);
        var conn = new ConnectionId(42UL);

        // Acquire 2 requests (uses all 2 burst tokens and hits 2 in-flight slots)
        Assert.True(limiter.TryAcquire(conn, tokenCost: 1.0));
        Assert.True(limiter.TryAcquire(conn, tokenCost: 1.0));
        Assert.False(limiter.TryAcquire(conn, tokenCost: 1.0));

        // Advance 1 second to refill 2 tokens, but in-flight is still 2 -> must still reject due to backpressure
        fakeTime.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.TryAcquire(conn, tokenCost: 1.0));

        // Release 1 in-flight slot -> now acquisition succeeds
        limiter.ReleaseInFlight(conn);
        Assert.True(limiter.TryAcquire(conn, tokenCost: 1.0));
        Assert.Equal(1, limiter.TrackedCount);
        Assert.True(limiter.Remove(conn));
        Assert.Equal(0, limiter.TrackedCount);
    }

    [Fact]
    public async Task MpscMailbox_CapacityBoundsAndMultiProducerConcurrency_DeliverAllMessagesLosslessly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpscMailbox<int>(0));

        var smallRing = new MpscMailbox<int>(capacityPowerOfTwo: 4);
        Assert.Equal(16, smallRing.Capacity);

        for (int i = 0; i < 16; i++)
        {
            Assert.True(smallRing.TryEnqueue(in i));
        }

        int extra = 99;
        Assert.False(smallRing.TryEnqueue(in extra));
        Assert.True(smallRing.TryDequeue(out int first));
        Assert.Equal(0, first);
        Assert.True(smallRing.TryEnqueue(in extra));

        // Multi-producer concurrent stress test (4 producers x 250 items = 1000 items)
        var concurrentRing = new MpscMailbox<int>(1024);
        int sum = await RunConcurrentProducersAndDrainAsync(concurrentRing, producerCount: 4, itemsPerProducer: 250);

        Assert.Equal(4 * (249 * 250 / 2), sum);
    }

    [Fact]
    public void SingleThreadSynchronizationContext_SendPostAndDrain_PreserveOwnerThreadAffinity()
    {
        var syncCtx = new SingleThreadSynchronizationContext(new CoreIndex(1), Environment.CurrentManagedThreadId);
        Assert.Equal(new CoreIndex(1), syncCtx.CoreIndex);
        Assert.True(syncCtx.IsOnOwnerThread);
        Assert.Same(syncCtx, syncCtx.CreateCopy());

        int counter = 0;
        // Send on owner thread executes synchronously
        syncCtx.Send(_ => counter += 10, null);
        Assert.Equal(10, counter);

        // Post and cross-thread Send enqueue into the work queue
        syncCtx.Post(_ => counter += 5, null);
        bool observedNonOwnerThread = false;
        var backgroundThread = new Thread(() =>
        {
            observedNonOwnerThread = !syncCtx.IsOnOwnerThread;
            syncCtx.Send(_ => counter += 20, null);
        });
        backgroundThread.Start();
        backgroundThread.Join();

        Assert.True(observedNonOwnerThread);
        Assert.Equal(10, counter);
        Assert.Equal(1, syncCtx.DrainContinuations(maxItems: 1));
        Assert.Equal(15, counter);
        Assert.Equal(1, syncCtx.DrainContinuations(maxItems: 10));
        Assert.Equal(35, counter);
    }

    [Fact]
    public async Task RoomActorAndRoomManager_EdgeCasesAndMultiRoomDisconnectCleanup_WorkCorrectly()
    {
        using var writer = new PooledBufferWriter(1024);
        var context = new HayabusaContext(CoreIndex.Zero, writer);
        var sq = new TransportSubmissionQueue(64);
        using var manager = new RoomManager(CoreIndex.Zero, context, sq);

        Assert.Equal(CoreIndex.Zero, manager.CoreIndex);
        Assert.Throws<ArgumentException>(() => manager.RegisterRoom(RoomId.Invalid, new BareRoomActor()));
        Assert.False(manager.TryGetRoom(new RoomId(999UL), out _));
        Assert.False(manager.DestroyRoom(new RoomId(999UL)));

        BareRoomActor r1 = manager.CreateAndRegisterRoom<BareRoomActor>(new RoomId(1UL));
        BareRoomActor r2 = manager.CreateAndRegisterRoom<BareRoomActor>(new RoomId(2UL));

        // Invalid and duplicate player joins
        Assert.False(r1.JoinPlayer(ConnectionId.Invalid));
        var player = new ConnectionId(77UL);
        Assert.True(r1.JoinPlayer(player));
        Assert.False(r1.JoinPlayer(player));
        Assert.Single(r1.Players);
        Assert.True(r2.JoinPlayer(player));

        // Base virtual methods return false by default
        Assert.False(await r1.TryDispatchRpcAsync(context, new MethodId(1), ReadOnlyMemory<byte>.Empty, writer));
        _ = HttpParser.TryParseRequest("GET / HTTP/1.1\r\n\r\n"u8, out HttpRequestView reqView);
        Assert.False(r1.TryDispatchHttp(context, in reqView, writer));

        // RemoveDisconnectedPlayer removes player from both rooms
        manager.RemoveDisconnectedPlayer(player);
        Assert.Equal(0, r1.PlayerCount);
        Assert.Equal(0, r2.PlayerCount);
        Assert.False(r1.LeavePlayer(player));

        // Broadcast with 0 players returns 0
        int dummySnap = 1;
        Assert.Equal(0, r1.BroadcastSnapshot(new MethodId(1), in dummySnap));
    }

    [Fact]
    public void CoreWorker_ManualTickAsyncContinuationAndDispose_BehaveDeterministically()
    {
        using var transport = new InMemoryTransport();
        var dispatcher = new RpcDispatcher();
        dispatcher.RegisterService(new YieldingAsyncEchoService());
        using var worker = new CoreWorker(new CoreWorkerConfig(new CoreIndex(1)), transport, dispatcher);

        int tickedCount = 0;
        worker.RegisterTickHandler(new DelegateTickHandler((_, _) => tickedCount++));
        worker.StepManualTick(new TickNumber(42UL));
        Assert.Equal(1, tickedCount);
        Assert.True(worker.LastObservedThreadId > 0);
        Assert.Equal(new CoreIndex(1), worker.CoreIndex);

        // Send frame to yielding async RPC service -> completes over two iterations via SingleThreadSynchronizationContext
        var conn = new ConnectionId(88UL);
        using var reqWriter = new PooledBufferWriter(64);
        PooledBufferWriter rw = reqWriter;
        int payloadVal = 123;
        FrameParser.WriteMemoryPackFrame(ref rw, new MethodId(77), RpcFrameFlags.None, in payloadVal);
        transport.EnqueueAccept(conn);
        transport.TryEnqueueReceive(conn, reqWriter.WrittenSpan);

        worker.StepIteration();
        worker.StepIteration();

        Assert.Single(transport.GetSentPayloads(conn));

        // Receive 0 bytes (EOF) evicts connection
        transport.TryEnqueueReceive(conn, ReadOnlySpan<byte>.Empty);
        worker.StepIteration();
        Assert.Equal(0, worker.ActiveConnectionCount);

        worker.Dispose();
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.StepIteration());
        Assert.Throws<ObjectDisposedException>(() => worker.StepManualTick(TickNumber.Zero));
    }

    [Theory]
    [InlineData("web", HayabusaServerRole.WebOnly)]
    [InlineData("webonly", HayabusaServerRole.WebOnly)]
    [InlineData("web-only", HayabusaServerRole.WebOnly)]
    [InlineData("http", HayabusaServerRole.WebOnly)]
    [InlineData("game", HayabusaServerRole.GameOnly)]
    [InlineData("gameonly", HayabusaServerRole.GameOnly)]
    [InlineData("game-only", HayabusaServerRole.GameOnly)]
    [InlineData("rpc", HayabusaServerRole.GameOnly)]
    [InlineData("both", HayabusaServerRole.WebAndGame)]
    [InlineData("all", HayabusaServerRole.WebAndGame)]
    [InlineData("hybrid", HayabusaServerRole.WebAndGame)]
    [InlineData("webandgame", HayabusaServerRole.WebAndGame)]
    [InlineData("web-and-game", HayabusaServerRole.WebAndGame)]
    [InlineData("unknown-value", HayabusaServerRole.WebOnly)]
    [InlineData("", HayabusaServerRole.GameOnly)]
    public void HayabusaServerOptions_ParseRoleOrDefault_ResolvesAllAliasesAndFallback(
        string raw,
        HayabusaServerRole expectedOrFallback)
    {
        HayabusaServerRole actual = HayabusaServerOptions.ParseRoleOrDefault(raw, expectedOrFallback);

        Assert.Equal(expectedOrFallback, actual);
    }

    [Fact]
    public void HayabusaServerOptions_StandaloneFlagsAndEnvVars_ConfigureOptionsCorrectly()
    {
        var opt1 = new HayabusaServerOptions();
        opt1.ApplyEnvironmentAndArgs(["--web-only"]);
        Assert.Equal(HayabusaServerRole.WebOnly, opt1.Role);

        var opt2 = new HayabusaServerOptions();
        opt2.ApplyEnvironmentAndArgs(["--game-only"]);
        Assert.Equal(HayabusaServerRole.GameOnly, opt2.Role);

        var opt3 = new HayabusaServerOptions();
        opt3.ApplyEnvironmentAndArgs(["--web-only", "--web-and-game", "--http-port", "not-an-int"]);
        Assert.Equal(HayabusaServerRole.WebAndGame, opt3.Role);
        Assert.Equal(5000, opt3.HttpPort);
        Assert.True(opt3.ResolvedWorkerCount >= 1);
    }

    [Fact]
    public async Task HayabusaApp_FluentRegistrationsAndRunAsyncWithCancellation_StartAndStopCleanly()
    {
        HayabusaAppBuilder builder = HayabusaApp.CreateBuilder(["--transport", "inmemory", "--workers", "2"])
            .UseLogger(NullLogger.Instance)
            .UseRole(HayabusaServerRole.WebAndGame)
            .UseThreadPerCore(o =>
            {
                o.TickRate = 30;
                o.HttpPort = 0;
                o.RpcPort = 0;
            });

        Assert.Equal(30, builder.Options.TickRate);

        using HayabusaApp app = builder.Build();
        app.MapService<BattleRoomService>()
           .MapService(new YieldingAsyncEchoService())
           .MapRpc<int, int>(101, static (_, x) => x + 1)
           .MapRpc<int, int>(102, static x => x + 2)
           .MapRpc(103, static () => 3)
           .MapGet("/ping", static ctx => ctx.WriteUtf8("PONG"u8))
           .MapPost("/echo", static ctx => ctx.WriteUtf8("ECHO"u8))
           .RegisterTickHandler(new DelegateTickHandler((_, _) => { }))
           .RegisterTickHandler(new DelegateTickHandler((_, _) => { }), CoreIndex.Zero);

        BattleRoomService pinnedRoom = app.RegisterRoom<BattleRoomService>(new RoomId(5UL));

        Assert.Equal(2, app.Workers.Count);
        Assert.NotNull(app.Options);
        Assert.NotNull(app.Dispatcher);
        Assert.Equal(new CoreIndex(1), pinnedRoom.CoreIndex);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await app.RunAsync(cts.Token);

        app.Dispose();
        app.Dispose();
    }

    private static async Task<int> RunConcurrentProducersAndDrainAsync(
        MpscMailbox<int> mailbox,
        int producerCount,
        int itemsPerProducer)
    {
        var tasks = new Task[producerCount];
        for (int p = 0; p < producerCount; p++)
        {
            tasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < itemsPerProducer; i++)
                {
                    while (!mailbox.TryEnqueue(in i))
                    {
                        Thread.SpinWait(1);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        int sum = 0;
        int count = 0;
        while (mailbox.TryDequeue(out int val))
        {
            sum += val;
            count++;
        }

        Assert.Equal(producerCount * itemsPerProducer, count);
        return sum;
    }

    private sealed class BareRoomActor : RoomActor
    {
    }

    private sealed class DelegateTickHandler(Action<CoreIndex, TickNumber> callback) : IGameLoopTickHandler
    {
        public void OnGameTick(CoreIndex coreIndex, TickNumber tick) => callback(coreIndex, tick);
    }

    private sealed class YieldingAsyncEchoService : IHayabusaRpcService
    {
        public async ValueTask<bool> TryDispatchRpcAsync(
            HayabusaContext context,
            MethodId methodId,
            ReadOnlyMemory<byte> payload,
            IBufferWriter<byte> responseWriter)
        {
            if (methodId.Value != 77)
            {
                return false;
            }

            await Task.Yield();
            FrameParser.WriteFrame(ref responseWriter, methodId, RpcFrameFlags.Response, payload.Span);
            return true;
        }

        public bool TryDispatchHttp(
            HayabusaContext context,
            in HttpRequestView request,
            IBufferWriter<byte> responseWriter) => false;
    }
}
