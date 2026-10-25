using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using Hayabusa.Core.Primitives;
using Hayabusa.Core.Transport;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Engine;
using Hayabusa.Runtime.Rooms;
using Hayabusa.Transport;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Hayabusa.Runtime.Hosting;

/// <summary>
/// Fluent builder for configuring and constructing a <see cref="HayabusaApp"/> server instance.
/// </summary>
public sealed class HayabusaAppBuilder
{
    private readonly HayabusaServerOptions _options = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HayabusaAppBuilder"/> class and loads environment and CLI settings.
    /// </summary>
    public HayabusaAppBuilder(string[]? args = null)
    {
        _options.ApplyEnvironmentAndArgs(args);
    }

    /// <summary>Gets the underlying server options.</summary>
    public HayabusaServerOptions Options => _options;

    /// <summary>
    /// Configures Thread-per-Core worker threads, tick frequency, and transport backend options.
    /// </summary>
    public HayabusaAppBuilder UseThreadPerCore(Action<HayabusaServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>
    /// Configures the structured <see cref="ILogger"/> (such as Cysharp <c>ZLogger</c>) used by worker loops and transports.
    /// </summary>
    public HayabusaAppBuilder UseLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _options.Logger = logger;
        return this;
    }

    /// <summary>
    /// Builds the configured <see cref="HayabusaApp"/> instance.
    /// </summary>
    public HayabusaApp Build() => new(_options);
}

/// <summary>
/// Represents a configured Hayabusa Web, MemoryPack RPC, and real-time Game Room server application.
/// Supports both Source-Generated <c>[HayabusaService]</c> classes and Minimal API endpoints (<c>MapRpc</c>, <c>MapGet</c>, <c>MapPost</c>).
/// </summary>
public sealed class HayabusaApp : IDisposable
{
    private readonly HayabusaServerOptions _options;
    private readonly RpcDispatcher _dispatcher = new();
    private readonly List<CoreWorker> _workers = new();
    private readonly List<ITransportBackend> _transports = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HayabusaApp"/> class.
    /// </summary>
    public HayabusaApp(HayabusaServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        InitializeWorkers();
    }

    /// <summary>Gets the server configuration options.</summary>
    public HayabusaServerOptions Options => _options;

    /// <summary>Gets the shared RPC and HTTP endpoint dispatcher.</summary>
    public RpcDispatcher Dispatcher => _dispatcher;

    /// <summary>Gets the dedicated Thread-per-Core workers managed by this application.</summary>
    public IReadOnlyList<CoreWorker> Workers => _workers;

    /// <summary>
    /// Creates a new <see cref="HayabusaAppBuilder"/> initialized with environment variables and CLI arguments.
    /// </summary>
    public static HayabusaAppBuilder CreateBuilder(string[]? args = null) => new(args);

    /// <summary>
    /// Registers a source-generated <see cref="IHayabusaRpcService"/> with the server dispatcher.
    /// </summary>
    public HayabusaApp MapService(IHayabusaRpcService service)
    {
        _dispatcher.RegisterService(service);
        return this;
    }

    /// <summary>
    /// Creates and registers an affinity-pinned <typeparamref name="TRoom"/> instance onto its deterministic worker core.
    /// Ownership of the created room is transferred to the target core's <see cref="RoomManager"/>.
    /// </summary>
    public TRoom RegisterRoom<TRoom>(RoomId roomId)
        where TRoom : RoomActor, new()
    {
        CoreIndex targetCore = roomId.ResolveCoreAffinity(_workers.Count);
        var room = new TRoom();
        try
        {
            _workers[targetCore.Value].RoomManager.RegisterRoom(roomId, room);
            _dispatcher.RegisterService(room);
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
    /// Maps a Minimal API binary MemoryPack RPC handler to <paramref name="methodId"/>.
    /// </summary>
    public HayabusaApp MapRpc<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        HayabusaRpcHandler<TRequest, TResponse> handler)
    {
        _dispatcher.MapRpc(methodId, handler);
        return this;
    }

    /// <summary>
    /// Maps a Minimal API HTTP GET endpoint handler to <paramref name="path"/>.
    /// </summary>
    public HayabusaApp MapGet(string path, HayabusaHttpHandler handler)
    {
        _dispatcher.MapHttp(HttpMethodKind.Get, path, handler);
        return this;
    }

    /// <summary>
    /// Maps a Minimal API HTTP POST endpoint handler to <paramref name="path"/>.
    /// </summary>
    public HayabusaApp MapPost(string path, HayabusaHttpHandler handler)
    {
        _dispatcher.MapHttp(HttpMethodKind.Post, path, handler);
        return this;
    }

    /// <summary>
    /// Starts all dedicated Thread-per-Core worker threads and runs until <paramref name="cancellationToken"/> is triggered.
    /// </summary>
    public ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BindPrimaryListenerIfNeeded();

        var threads = new Thread[_workers.Count];
        for (int i = 0; i < _workers.Count; i++)
        {
            CoreWorker worker = _workers[i];
            var thread = new Thread(() => worker.RunBlockingLoop(cancellationToken))
            {
                Name = ZString.Concat("Hayabusa-Core-", worker.CoreIndex.Value),
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            threads[i] = thread;
            thread.Start();
        }

        _options.Logger?.ZLogInformation($"Hayabusa server started with {_workers.Count} Thread-per-Core workers at {_options.TickRate}Hz.");
        WaitForWorkerThreads(threads, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _workers.Count; i++)
        {
            _workers[i].Dispose();
        }

        for (int i = 0; i < _transports.Count; i++)
        {
            _transports[i].Dispose();
        }

        _workers.Clear();
        _transports.Clear();
    }

    private void InitializeWorkers()
    {
        int count = _options.ResolvedWorkerCount;
        for (int i = 0; i < count; i++)
        {
            ITransportBackend transport = TransportFactory.Create(_options.Transport, logger: _options.Logger);
            _transports.Add(transport);
            var config = new CoreWorkerConfig(new CoreIndex(i), _options.TickRate, Logger: _options.Logger);
            _workers.Add(new CoreWorker(config, transport, _dispatcher));
        }
    }

    private void BindPrimaryListenerIfNeeded()
    {
        if (_transports.Count > 0 && _transports[0] is SocketEventTransport socketTransport && _options.HttpPort > 0)
        {
            if (IPAddress.TryParse(_options.BindAddress, out IPAddress? ip))
            {
                socketTransport.BindAndListen(new IPEndPoint(ip, _options.HttpPort));
            }
        }
    }

    private static void WaitForWorkerThreads(Thread[] threads, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.WaitHandle.WaitOne();
        }

        for (int i = 0; i < threads.Length; i++)
        {
            if (threads[i].IsAlive)
            {
                threads[i].Join(TimeSpan.FromSeconds(2));
            }
        }
    }
}
