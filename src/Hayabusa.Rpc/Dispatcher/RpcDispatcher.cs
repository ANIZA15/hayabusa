using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using MemoryPack;

namespace Hayabusa.Rpc.Dispatcher;

/// <summary>
/// Delegate for synchronous zero-allocation Minimal API RPC handlers.
/// </summary>
public delegate TResponse HayabusaRpcHandler<in TRequest, out TResponse>(HayabusaContext context, TRequest request);

/// <summary>
/// Delegate for synchronous zero-allocation Minimal API HTTP handlers.
/// </summary>
public delegate void HayabusaHttpHandler(HayabusaContext context);

/// <summary>
/// High-performance dispatcher supporting both Source-Generated <see cref="IHayabusaRpcService"/> implementations
/// and Minimal API endpoint registrations (<c>MapRpc</c> / <c>MapGet</c> / <c>MapPost</c>).
/// </summary>
public sealed class RpcDispatcher
{
    private delegate bool MinimalRpcInvokeDelegate(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlySpan<byte> payload,
        PooledBufferWriter responseWriter);

    private readonly Dictionary<int, MinimalRpcInvokeDelegate> _minimalRpcHandlers = new();
    private readonly List<HttpRouteEntry> _httpRoutes = new();
    private readonly List<IHayabusaRpcService> _generatedServices = new();

    /// <summary>
    /// Registers a source-generated <see cref="IHayabusaRpcService"/> instance.
    /// </summary>
    public void RegisterService(IHayabusaRpcService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _generatedServices.Add(service);
    }

    /// <summary>
    /// Registers a Minimal API style binary MemoryPack RPC handler for <paramref name="methodId"/>.
    /// </summary>
    public void MapRpc<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        HayabusaRpcHandler<TRequest, TResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _minimalRpcHandlers[methodId] = (ctx, id, payload, writer) =>
        {
            TRequest? req = MemoryPackSerializer.Deserialize<TRequest>(payload);
            TResponse res = handler(ctx, req!);
            FrameParser.WriteMemoryPackFrame(ref writer, id, RpcFrameFlags.Response, in res);
            return true;
        };
    }

    /// <summary>
    /// Registers a Minimal API style HTTP endpoint handler.
    /// </summary>
    public void MapHttp(HttpMethodKind method, string path, HayabusaHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);
        _httpRoutes.Add(new HttpRouteEntry(method, Encoding.UTF8.GetBytes(path), handler));
    }

    /// <summary>
    /// Synchronously dispatches an incoming binary RPC frame with zero heap allocations on the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDispatchSync(
        HayabusaContext context,
        in FrameSlice frame,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        int id = frame.Header.MethodId.Value;
        if (_minimalRpcHandlers.TryGetValue(id, out MinimalRpcInvokeDelegate? minimalHandler))
        {
            return minimalHandler(context, frame.Header.MethodId, frame.Payload, responseWriter);
        }

        WriteUnknownMethodErrorFrame(ref responseWriter, frame.Header.MethodId);
        return false;
    }

    /// <summary>
    /// Dispatches an incoming binary RPC frame across registered source-generated services and Minimal API handlers.
    /// </summary>
    public async ValueTask<bool> DispatchAsync(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlyMemory<byte> payload,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        for (int i = 0; i < _generatedServices.Count; i++)
        {
            if (await _generatedServices[i].TryDispatchRpcAsync(context, methodId, payload, responseWriter))
            {
                return true;
            }
        }

        if (_minimalRpcHandlers.TryGetValue(methodId.Value, out MinimalRpcInvokeDelegate? minimalHandler))
        {
            return minimalHandler(context, methodId, payload.Span, responseWriter);
        }

        WriteUnknownMethodErrorFrame(ref responseWriter, methodId);
        return false;
    }

    /// <summary>
    /// Dispatches a parsed HTTP request across registered source-generated services and Minimal API routes.
    /// </summary>
    public bool DispatchHttp(
        HayabusaContext context,
        in HttpRequestView request,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        for (int i = 0; i < _generatedServices.Count; i++)
        {
            if (_generatedServices[i].TryDispatchHttp(context, in request, responseWriter))
            {
                return true;
            }
        }

        for (int i = 0; i < _httpRoutes.Count; i++)
        {
            HttpRouteEntry route = _httpRoutes[i];
            if (route.Matches(request.Method, request.Path))
            {
                route.Handler(context);
                return true;
            }
        }

        HttpResponseWriter.WriteNotFound(ref responseWriter);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUnknownMethodErrorFrame(ref PooledBufferWriter writer, MethodId methodId)
    {
        FrameParser.WriteFrame(ref writer, methodId, RpcFrameFlags.Error, ReadOnlySpan<byte>.Empty);
    }

    private readonly struct HttpRouteEntry
    {
        private readonly HttpMethodKind _method;
        private readonly byte[] _pathUtf8;

        public HttpRouteEntry(HttpMethodKind method, byte[] pathUtf8, HayabusaHttpHandler handler)
        {
            _method = method;
            _pathUtf8 = pathUtf8;
            Handler = handler;
        }

        public HayabusaHttpHandler Handler { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(HttpMethodKind method, scoped ReadOnlySpan<byte> path) =>
            _method == method && path.SequenceEqual(_pathUtf8);
    }
}
