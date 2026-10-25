using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc.Http;

namespace Hayabusa.Rpc;

/// <summary>
/// Lightweight request execution context passed to RPC and HTTP handlers on a worker thread.
/// Avoids the heavy per-request allocation and feature collection overhead of ASP.NET Core's <c>HttpContext</c>.
/// </summary>
public sealed class HayabusaContext
{
    private readonly PooledBufferWriter _responseWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="HayabusaContext"/> class bound to a specific worker core.
    /// </summary>
    /// <param name="coreIndex">The worker thread core index owning this context.</param>
    /// <param name="responseWriter">The reusable pinned buffer writer for responses.</param>
    public HayabusaContext(CoreIndex coreIndex, PooledBufferWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(responseWriter);
        CoreIndex = coreIndex;
        _responseWriter = responseWriter;
    }

    /// <summary>Gets the worker core index executing the current handler.</summary>
    public CoreIndex CoreIndex { get; }

    /// <summary>Gets the connection identifier of the caller.</summary>
    public ConnectionId ConnectionId { get; internal set; }

    /// <summary>Gets the target room identifier if the request is bound to a room.</summary>
    public RoomId RoomId { get; internal set; }

    /// <summary>Gets the current game loop tick number on this worker core.</summary>
    public TickNumber CurrentTick { get; internal set; }

    /// <summary>Gets the reusable response buffer writer for zero-allocation output.</summary>
    public PooledBufferWriter ResponseWriter => _responseWriter;

    /// <summary>
    /// Resets the context fields for the next incoming frame on the same worker thread with zero allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bind(ConnectionId connectionId, RoomId roomId, TickNumber currentTick)
    {
        ConnectionId = connectionId;
        RoomId = roomId;
        CurrentTick = currentTick;
        _responseWriter.Clear();
    }

    /// <summary>
    /// Writes a UTF-8 HTTP 200 OK body directly into the response buffer without allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8(scoped ReadOnlySpan<byte> utf8Body)
    {
        PooledBufferWriter writer = _responseWriter;
        HttpResponseWriter.WriteOkUtf8(ref writer, utf8Body);
    }

    /// <summary>
    /// Writes a formatted UTF-8 HTTP 200 OK body using Cysharp <c>ZString</c> with zero heap allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZString<TArg>(string format, TArg arg)
    {
        PooledBufferWriter writer = _responseWriter;
        HttpResponseWriter.WriteFormattedOk(ref writer, format, arg);
    }
}

/// <summary>
/// Interface implemented by compile-time source-generated Hayabusa RPC and HTTP services.
/// </summary>
public interface IHayabusaRpcService
{
    /// <summary>
    /// Dispatches a binary MemoryPack RPC frame by <paramref name="methodId"/> and writes the serialized response into <paramref name="responseWriter"/>.
    /// </summary>
    ValueTask<bool> TryDispatchRpcAsync(
        HayabusaContext context,
        MethodId methodId,
        ReadOnlyMemory<byte> payload,
        IBufferWriter<byte> responseWriter);

    /// <summary>
    /// Dispatches an HTTP request by <paramref name="request"/> and writes the HTTP response into <paramref name="responseWriter"/>.
    /// </summary>
    bool TryDispatchHttp(
        HayabusaContext context,
        in HttpRequestView request,
        IBufferWriter<byte> responseWriter);
}
