using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Hosting;
using Hayabusa.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hayabusa.Tests;

public sealed class TransportLayerExtendedTests
{
    [Fact]
    public void InMemoryTransport_ExhaustedSlabOversizedPayloadAndSubmissions_BehaveCorrectly()
    {
        using var smallPool = new PinnedSlabPool(blockSize: 16, blockCount: 1);
        using var transport = new InMemoryTransport(smallPool);
        var connId = new ConnectionId(10UL);

        Assert.Equal("inmemory", transport.Name);
        Assert.Same(smallPool, transport.SlabPool);
        Assert.Empty(transport.GetSentPayloads(connId));

        // Oversized payload (> 16 bytes) returns false and returns the rented slot immediately
        byte[] oversized = new byte[32];
        Assert.False(transport.TryEnqueueReceive(connId, oversized));
        Assert.Equal(0, smallPool.OutstandingRentals);

        // Valid payload occupies the single slab block
        Assert.True(transport.TryEnqueueReceive(connId, [1, 2, 3]));
        Assert.Equal(1, smallPool.OutstandingRentals);

        // Next receive fails because pool is exhausted
        Assert.False(transport.TryEnqueueReceive(connId, [4]));

        // Drain completion and return the slab token via SubmitAndWait
        Span<TransportCompletionEvent> cqes = stackalloc TransportCompletionEvent[4];
        int drained = transport.DrainCompletions(cqes);
        Assert.Equal(1, drained);

        var sendEntry = new TransportSubmissionEntry(
            TransportOperationKind.Send,
            connId,
            new byte[] { 9, 9 },
            cqes[0].SlabToken);
        var disconnectEntry = new TransportSubmissionEntry(
            TransportOperationKind.Disconnect,
            connId,
            ReadOnlyMemory<byte>.Empty);

        int submitted = transport.SubmitAndWait([sendEntry, disconnectEntry], TimeSpan.Zero);

        Assert.Equal(2, submitted);
        Assert.Equal(1, transport.SubmitBatchCount);
        Assert.Equal(0, smallPool.OutstandingRentals);
        Assert.Single(transport.GetSentPayloads(connId));
        Assert.True(transport.IsDisconnected(connId));

        // Re-accept clears disconnected state
        transport.EnqueueAccept(connId);
        Assert.False(transport.IsDisconnected(connId));
    }

    [Fact]
    public void InMemoryTransport_AfterDispose_ThrowsObjectDisposedException()
    {
        var transport = new InMemoryTransport();
        transport.Dispose();
        transport.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.EnqueueAccept(new ConnectionId(1UL)));
        Assert.Throws<ObjectDisposedException>(() => transport.TryEnqueueReceive(new ConnectionId(1UL), [1]));
        Assert.Throws<ObjectDisposedException>(() => transport.EnqueueDisconnect(new ConnectionId(1UL)));
        Assert.Throws<ObjectDisposedException>(() => transport.DrainCompletions([]));
        Assert.Throws<ObjectDisposedException>(() => transport.SubmitAndWait([], TimeSpan.Zero));
    }

    [Theory]
    [InlineData("inmemory", "inmemory")]
    [InlineData("  INMEMORY ", "inmemory")]
    [InlineData("socket", "socket")]
    [InlineData("SOCKET", "socket")]
    public void TransportFactory_CreateWithExplicitModes_ReturnsExpectedBackend(string mode, string expectedName)
    {
        using ITransportBackend backend = TransportFactory.Create(mode, logger: NullLogger.Instance);

        Assert.Equal(expectedName, backend.Name);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("io_uring")]
    [InlineData(null)]
    [InlineData("")]
    public void TransportFactory_CreateWithAutoOrIoUring_ReturnsIoUringOrSocketGracefully(string? mode)
    {
        using ITransportBackend backend = TransportFactory.Create(mode, logger: NullLogger.Instance);

        Assert.True(backend.Name is "io_uring" or "socket");
    }

    [Fact]
    public async Task SocketEventTransport_FullLoopbackHttpRoundtripAndDisconnect_SucceedsWithZeroSlabLeak()
    {
        int port = AllocateFreeLoopbackPort();
        using var transport = new SocketEventTransport();
        transport.BindAndListen(new IPEndPoint(IPAddress.Loopback, port), enableReusePort: true);

        var dispatcher = new RpcDispatcher();
        dispatcher.MapHttp(HttpMethodKind.Get, "/hello", static ctx => ctx.WriteUtf8("WORLD"u8));
        using var worker = new CoreWorker(
            new CoreWorkerConfig(CoreIndex.Zero, Role: HayabusaServerRole.WebOnly),
            transport,
            dispatcher);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();

        byte[] reqBytes = "GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(reqBytes);

        byte[] readBuffer = new byte[512];
        int bytesRead = await PumpWorkerUntilClientReadsAsync(worker, stream, readBuffer);
        string responseText = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);

        Assert.Contains("200 OK", responseText, StringComparison.Ordinal);
        Assert.Contains("WORLD", responseText, StringComparison.Ordinal);

        // Close client and pump worker so disconnect is processed cleanly
        client.Close();
        PumpWorkerIterations(worker, 5);
        Assert.Equal(0, transport.SlabPool.OutstandingRentals);
    }

    [Fact]
    public void SocketEventTransport_AfterDispose_ThrowsObjectDisposedException()
    {
        var transport = new SocketEventTransport();
        transport.Dispose();
        transport.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.BindAndListen(new IPEndPoint(IPAddress.Loopback, 0)));
        Assert.Throws<ObjectDisposedException>(() => transport.DrainCompletions([]));
        Assert.Throws<ObjectDisposedException>(() => transport.SubmitAndWait([], TimeSpan.Zero));
    }

    private static int AllocateFreeLoopbackPort()
    {
        using var tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tempSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
    }

    private static async Task<int> PumpWorkerUntilClientReadsAsync(
        CoreWorker worker,
        NetworkStream stream,
        byte[] buffer)
    {
        ValueTask<int> readTask = stream.ReadAsync(buffer);
        for (int i = 0; i < 50 && !readTask.IsCompleted; i++)
        {
            worker.StepIteration(allowWaitForDeadline: true);
            await Task.Delay(2);
        }

        return await readTask;
    }

    private static void PumpWorkerIterations(CoreWorker worker, int count)
    {
        for (int i = 0; i < count; i++)
        {
            worker.StepIteration(allowWaitForDeadline: true);
        }
    }
}
