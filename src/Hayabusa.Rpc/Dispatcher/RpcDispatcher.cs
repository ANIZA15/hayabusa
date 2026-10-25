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
using Hayabusa.Rpc.Security;
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
/// Uses a direct $O(1)$ array jump table for <see cref="MethodId"/> values in <c>0..1023</c> to eliminate dictionary hashing overhead,
/// and bypasses async state machines when RPC handlers complete synchronously.
/// </summary>
public sealed class RpcDispatcher
{
    private const int FastJumpTableSize = 1024;

    private delegate bool MinimalRpcInvokeDelegate(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlySpan<byte> payload,
        PooledBufferWriter responseWriter);

    private readonly MinimalRpcInvokeDelegate?[] _fastRpcJumpTable = new MinimalRpcInvokeDelegate?[FastJumpTableSize];
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
        MinimalRpcInvokeDelegate invoker = (ctx, id, payload, writer) =>
        {
            TRequest? req = DeserializePayloadChecked<TRequest>(payload);
            TResponse res = handler(ctx, req!);
            FrameParser.WriteMemoryPackFrame(ref writer, id, RpcFrameFlags.Response, in res);
            return true;
        };

        RegisterMinimalRpcInvoker(methodId, invoker);
    }

    /// <summary>
    /// Registers a compact context-free binary MemoryPack RPC handler for <paramref name="methodId"/>.
    /// </summary>
    public void MapRpc<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        Func<TRequest, TResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        MinimalRpcInvokeDelegate invoker = (_, id, payload, writer) =>
        {
            TRequest? req = DeserializePayloadChecked<TRequest>(payload);
            TResponse res = handler(req!);
            FrameParser.WriteMemoryPackFrame(ref writer, id, RpcFrameFlags.Response, in res);
            return true;
        };

        RegisterMinimalRpcInvoker(methodId, invoker);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TRequest? DeserializePayloadChecked<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest>(
        scoped ReadOnlySpan<byte> payload)
    {
        if ((typeof(TRequest) == typeof(string) || typeof(TRequest).IsArray)
            && !MemoryPackPayloadGuard.ValidateCollectionHeader(payload))
        {
            MemoryPackSerializationException.ThrowMessage("Rejected MemoryPack collection/string length bomb.");
        }

        return MemoryPackSerializer.Deserialize<TRequest>(payload);
    }

    /// <summary>
    /// Registers a parameterless binary MemoryPack RPC handler for <paramref name="methodId"/>.
    /// </summary>
    public void MapRpc<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        Func<TResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        MinimalRpcInvokeDelegate invoker = (_, id, _, writer) =>
        {
            TResponse res = handler();
            FrameParser.WriteMemoryPackFrame(ref writer, id, RpcFrameFlags.Response, in res);
            return true;
        };

        RegisterMinimalRpcInvoker(methodId, invoker);
    }

    private void RegisterMinimalRpcInvoker(int methodId, MinimalRpcInvokeDelegate invoker)
    {
        if ((uint)methodId < FastJumpTableSize)
        {
            _fastRpcJumpTable[methodId] = invoker;
        }

        _minimalRpcHandlers[methodId] = invoker;
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

    [ThreadStatic]
    private static PooledBufferWriter? t_httpRpcScratchWriter;

    /// <summary>
    /// Synchronously dispatches an incoming binary RPC frame against registered Minimal API handlers without writing an error frame on miss.
    /// Used by <c>CoreWorker</c> before falling back to source-generated <see cref="IHayabusaRpcService"/> dispatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDispatchMinimalSync(
        HayabusaContext context,
        in FrameSlice frame,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        MinimalRpcInvokeDelegate? minimalHandler = ResolveMinimalHandler(frame.Header.MethodId.Value);
        return minimalHandler is not null && minimalHandler(context, frame.Header.MethodId, frame.Payload, responseWriter);
    }

    /// <summary>
    /// Synchronously dispatches an incoming binary RPC frame with zero heap allocations on the hot path.
    /// Uses $O(1)$ direct array indexing for method IDs under 1024.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDispatchSync(
        HayabusaContext context,
        in FrameSlice frame,
        PooledBufferWriter responseWriter)
    {
        if (TryDispatchMinimalSync(context, in frame, responseWriter))
        {
            return true;
        }

        WriteUnknownMethodErrorFrame(ref responseWriter, frame.Header.MethodId);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MinimalRpcInvokeDelegate? ResolveMinimalHandler(int id)
    {
        if ((uint)id < FastJumpTableSize)
        {
            return _fastRpcJumpTable[id];
        }

        return _minimalRpcHandlers.TryGetValue(id, out MinimalRpcInvokeDelegate? fallback) ? fallback : null;
    }

    /// <summary>
    /// Dispatches an incoming binary RPC frame across registered source-generated services and Minimal API handlers.
    /// Avoids async state machine overhead when services complete synchronously.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<bool> DispatchAsync(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlyMemory<byte> payload,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        for (int i = 0; i < _generatedServices.Count; i++)
        {
            ValueTask<bool> serviceTask = _generatedServices[i].TryDispatchRpcAsync(context, methodId, payload, responseWriter);
            if (!serviceTask.IsCompletedSuccessfully)
            {
                return AwaitGeneratedServicesSlowAsync(serviceTask, i, (context, methodId, payload, responseWriter));
            }

            if (serviceTask.Result)
            {
                return new ValueTask<bool>(true);
            }
        }

        return new ValueTask<bool>(TryDispatchMinimalOrError(context, methodId, payload.Span, responseWriter));
    }

    private async ValueTask<bool> AwaitGeneratedServicesSlowAsync(
        ValueTask<bool> pendingTask,
        int startIndex,
        (HayabusaContext Context, MethodId MethodId, ReadOnlyMemory<byte> Payload, PooledBufferWriter ResponseWriter) call)
    {
        if (await pendingTask)
        {
            return true;
        }

        for (int i = startIndex + 1; i < _generatedServices.Count; i++)
        {
            if (await _generatedServices[i].TryDispatchRpcAsync(call.Context, call.MethodId, call.Payload, call.ResponseWriter))
            {
                return true;
            }
        }

        return TryDispatchMinimalOrError(call.Context, call.MethodId, call.Payload.Span, call.ResponseWriter);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDispatchMinimalOrError(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlySpan<byte> payload,
        PooledBufferWriter responseWriter)
    {
        MinimalRpcInvokeDelegate? minimalHandler = ResolveMinimalHandler(methodId.Value);
        if (minimalHandler is not null)
        {
            return minimalHandler(context, methodId, payload, responseWriter);
        }

        WriteUnknownMethodErrorFrame(ref responseWriter, methodId);
        return false;
    }

    /// <summary>
    /// Dispatches a parsed HTTP request across CORS preflight, HTTP-tunneled RPC (<c>POST /rpc</c>),
    /// registered source-generated services, and Minimal API routes.
    /// </summary>
    public bool DispatchHttp(
        HayabusaContext context,
        in HttpRequestView request,
        PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(responseWriter);

        if (request.Method == HttpMethodKind.Options)
        {
            HttpResponseWriter.WriteCorsPreflight204(ref responseWriter);
            return true;
        }

        if (TryDispatchHttpRpcTunnel(context, in request, responseWriter))
        {
            return true;
        }

        return TryDispatchRegisteredHttpRoutes(context, in request, responseWriter);
    }

    private bool TryDispatchRegisteredHttpRoutes(
        HayabusaContext context,
        in HttpRequestView request,
        PooledBufferWriter responseWriter)
    {
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

    private bool TryDispatchHttpRpcTunnel(
        HayabusaContext context,
        in HttpRequestView request,
        PooledBufferWriter responseWriter)
    {
        if (request.Method != HttpMethodKind.Post || !request.Path.SequenceEqual("/rpc"u8))
        {
            return false;
        }

        FrameParseError err = FrameParser.TryParse(request.Body, RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err != FrameParseError.None)
        {
            HttpResponseWriter.WriteBadRequest(ref responseWriter);
            return true;
        }

        try
        {
            ExecuteHttpTunneledRpc(context, in frame, responseWriter);
        }
        catch (MemoryPackSerializationException)
        {
            responseWriter.Clear();
            HttpResponseWriter.WriteBadRequest(ref responseWriter);
        }

        return true;
    }

    private void ExecuteHttpTunneledRpc(
        HayabusaContext context,
        in FrameSlice frame,
        PooledBufferWriter responseWriter)
    {
        PooledBufferWriter scratch = GetHttpRpcScratchWriter();
        if (!TryDispatchMinimalSync(context, in frame, scratch))
        {
            ReadOnlyMemory<byte> payloadCopy = frame.Payload.ToArray();
            CompleteHttpRpcDispatch(DispatchAsync(context, frame.Header.MethodId, payloadCopy, scratch));
        }

        HttpResponseWriter.WriteOkMemoryPack(ref responseWriter, scratch.WrittenSpan);
    }

    private static void CompleteHttpRpcDispatch(ValueTask<bool> dispatchTask)
    {
        if (dispatchTask.IsCompleted)
        {
            _ = dispatchTask.GetAwaiter().GetResult();
            return;
        }

        _ = dispatchTask.AsTask().GetAwaiter().GetResult();
    }

    private static PooledBufferWriter GetHttpRpcScratchWriter()
    {
        PooledBufferWriter? writer = t_httpRpcScratchWriter;
        if (writer is null)
        {
            writer = new PooledBufferWriter(16 * 1024);
            t_httpRpcScratchWriter = writer;
        }
        else
        {
            writer.Clear();
        }

        return writer;
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
            _method == method && path.Length == _pathUtf8.Length && path.SequenceEqual(_pathUtf8);
    }
}
