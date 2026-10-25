using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc;
using Hayabusa.Rpc.Client;
using Hayabusa.Rpc.Framing;
using Hayabusa.Rpc.Http;
using Hayabusa.Runtime.Engine;
using Hayabusa.Transport;

namespace Hayabusa.Runtime.Hosting;

/// <summary>
/// Zero-socket hybrid bridge enabling ASP.NET Core (Kestrel / Blazor Server / Minimal API) and MagicOnion-style clients
/// to invoke <see cref="HayabusaApp"/> services, HTTP endpoints, and real-time <c>RoomActor</c> broadcasts in-process.
/// </summary>
public static class HayabusaHybridBridge
{
    /// <summary>
    /// Dispatches a raw 12-byte Hayabusa RPC frame in-process against <paramref name="app"/> and writes the response frame into <paramref name="responseWriter"/>.
    /// Ideal for bridging Kestrel HTTP/WebSocket/gRPC endpoints or Blazor Server directly into Hayabusa with zero socket overhead.
    /// </summary>
    public static async ValueTask<bool> DispatchRawRpcFrameAsync(
        HayabusaApp app,
        ReadOnlyMemory<byte> rawFrame,
        PooledBufferWriter responseWriter,
        ConnectionId callerId = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(responseWriter);

        FrameParseError err = FrameParser.TryParse(rawFrame.Span, RpcHeader.DefaultMaxFrameSize, out FrameSlice slice);
        if (err != FrameParseError.None)
        {
            return false;
        }

        ConnectionId effectiveConn = callerId.IsValid ? callerId : new ConnectionId(1UL);
        var context = new HayabusaContext(CoreIndex.Zero, responseWriter);
        context.Bind(effectiveConn, RoomId.Invalid, TickNumber.Zero, clearWriter: false);

        if (app.Dispatcher.TryDispatchMinimalSync(context, in slice, responseWriter))
        {
            return true;
        }

        ReadOnlyMemory<byte> payloadSlice = rawFrame.Slice(RpcHeader.Size, slice.Header.PayloadLength);
        return await app.Dispatcher.DispatchAsync(context, slice.Header.MethodId, payloadSlice, responseWriter);
    }

    /// <summary>
    /// Dispatches a raw HTTP/1.1 request (including CORS <c>OPTIONS</c> and <c>POST /rpc</c> binary RPC tunneling)
    /// in-process against <paramref name="app"/> and writes the HTTP response into <paramref name="responseWriter"/>.
    /// </summary>
    public static bool DispatchRawHttpRequest(
        HayabusaApp app,
        ReadOnlySpan<byte> rawHttpBytes,
        PooledBufferWriter responseWriter,
        ConnectionId callerId = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(responseWriter);

        HttpParseError err = HttpParser.TryParseRequest(rawHttpBytes, out HttpRequestView req);
        if (err != HttpParseError.None)
        {
            HttpResponseWriter.WriteBadRequest(ref responseWriter);
            return false;
        }

        ConnectionId effectiveConn = callerId.IsValid ? callerId : new ConnectionId(1UL);
        var context = new HayabusaContext(CoreIndex.Zero, responseWriter);
        context.Bind(effectiveConn, RoomId.Invalid, TickNumber.Zero, clearWriter: false);
        return app.Dispatcher.DispatchHttp(context, in req, responseWriter);
    }

    /// <summary>
    /// Forwards all pending server-to-client broadcast frames emitted on an <see cref="InMemoryTransport"/>
    /// for <paramref name="playerConnectionId"/> into <paramref name="client"/>'s registered <c>[HayabusaHubReceiver]</c> callbacks.
    /// </summary>
    public static int PumpInMemoryBroadcastsToClient(
        InMemoryTransport transport,
        ConnectionId playerConnectionId,
        HayabusaRpcClient client)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(client);

        IReadOnlyList<byte[]> payloads = transport.GetSentPayloads(playerConnectionId);
        int dispatched = 0;
        for (int i = 0; i < payloads.Count; i++)
        {
            dispatched += client.ProcessBroadcastFrames(payloads[i]);
        }

        return dispatched;
    }
}
