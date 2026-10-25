using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using Hayabusa.Rpc.Dispatcher;
using Hayabusa.Rpc.Framing;
using MemoryPack;

namespace Hayabusa.Rpc.Client;

/// <summary>
/// High-performance binary MemoryPack RPC and StreamingHub client for Hayabusa servers.
/// Supports raw TCP connections (<see cref="ConnectAsync"/>), HTTP/1.1 &amp; Kestrel/WASM RPC tunneling (<see cref="CreateHttp"/>),
/// and zero-socket in-memory dispatch (<see cref="CreateInMemory"/>).
/// Ownership: Caller must call <see cref="Dispose"/> to close the underlying socket and return pooled buffers.
/// </summary>
public sealed class HayabusaRpcClient : IDisposable
{
    private readonly TcpClient? _tcpClient;
    private readonly NetworkStream? _stream;
    private readonly HttpClient? _httpClient;
    private readonly string _httpEndpoint = "/rpc";
    private readonly RpcDispatcher? _inMemoryDispatcher;
    private readonly HayabusaContext? _inMemoryContext;
    private readonly Dictionary<MethodId, Action<ReadOnlyMemory<byte>>> _broadcastHandlers = new();
    private readonly PooledBufferWriter _sendWriter = new(4096);
    private readonly PooledBufferWriter _inMemoryResponseWriter = new(4096);
    private readonly byte[] _receiveBuffer = new byte[RpcHeader.DefaultMaxFrameSize * 2];
    private int _bufferedBytes;
    private bool _disposed;

    private HayabusaRpcClient(TcpClient tcpClient, NetworkStream stream)
    {
        _tcpClient = tcpClient;
        _stream = stream;
    }

    private HayabusaRpcClient(RpcDispatcher dispatcher)
    {
        _inMemoryDispatcher = dispatcher;
        _inMemoryContext = new HayabusaContext(CoreIndex.Zero, _inMemoryResponseWriter);
    }

    private HayabusaRpcClient(HttpClient httpClient, string endpoint)
    {
        _httpClient = httpClient;
        _httpEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "/rpc" : endpoint;
    }

    /// <summary>
    /// Connects to a remote or local Hayabusa RPC server over TCP with <c>TCP_NODELAY</c> enabled.
    /// </summary>
    public static async ValueTask<HayabusaRpcClient> ConnectAsync(
        string host,
        int port = 5001,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var tcpClient = new TcpClient { NoDelay = true };
        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken);
            NetworkStream stream = tcpClient.GetStream();
            var client = new HayabusaRpcClient(tcpClient, stream);
            tcpClient = null!;
            return client;
        }
        finally
        {
            tcpClient?.Dispose();
        }
    }

    /// <summary>
    /// Creates an in-memory <see cref="HayabusaRpcClient"/> bound directly to an <see cref="RpcDispatcher"/>
    /// for deterministic unit testing and in-process invocation (e.g. Blazor Server / ASP.NET Core hybrid) without opening TCP sockets.
    /// </summary>
    public static HayabusaRpcClient CreateInMemory(RpcDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new HayabusaRpcClient(dispatcher);
    }

    /// <summary>
    /// Creates an HTTP-tunneled <see cref="HayabusaRpcClient"/> over <paramref name="httpClient"/> (targeting <c>POST /rpc</c>)
    /// for environments where raw TCP sockets are unavailable (such as Blazor WebAssembly, Unity WebGL, or Kestrel reverse proxies).
    /// </summary>
    public static HayabusaRpcClient CreateHttp(HttpClient httpClient, string endpoint = "/rpc")
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        return new HayabusaRpcClient(httpClient, endpoint);
    }

    /// <summary>
    /// Subscribes a raw payload callback to server-pushed <see cref="RpcFrameFlags.Broadcast"/> frames for <paramref name="methodId"/>.
    /// Returns an <see cref="IDisposable"/> token compatible with Cysharp <c>R3.DisposableBag</c>.
    /// </summary>
    public IDisposable SubscribeBroadcast(MethodId methodId, Action<ReadOnlyMemory<byte>> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);

        if (_broadcastHandlers.TryGetValue(methodId, out Action<ReadOnlyMemory<byte>>? existing))
        {
            _broadcastHandlers[methodId] = existing + handler;
        }
        else
        {
            _broadcastHandlers[methodId] = handler;
        }

        return new BroadcastSubscription(this, methodId, handler);
    }

    /// <summary>
    /// Subscribes a strongly-typed <c>MemoryPack</c>-deserialized callback to server-pushed <see cref="RpcFrameFlags.Broadcast"/> frames for <paramref name="methodId"/>.
    /// Returns an <see cref="IDisposable"/> token compatible with Cysharp <c>R3.DisposableBag</c>.
    /// </summary>
    public IDisposable SubscribeBroadcast<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TPayload>(
        MethodId methodId,
        Action<TPayload> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeBroadcast(
            methodId,
            payload =>
            {
                TPayload? value = MemoryPackSerializer.Deserialize<TPayload>(payload.Span);
                handler(value!);
            });
    }

    /// <summary>
    /// Parses all complete <see cref="RpcFrameFlags.Broadcast"/> frames in <paramref name="rawFrames"/>
    /// and invokes any registered broadcast/receiver callbacks. Returns the number of broadcast frames dispatched.
    /// </summary>
    public int ProcessBroadcastFrames(ReadOnlySpan<byte> rawFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int dispatched = 0;
        ReadOnlySpan<byte> cursor = rawFrames;

        while (!cursor.IsEmpty)
        {
            FrameParseError err = FrameParser.TryParse(cursor, RpcHeader.DefaultMaxFrameSize, out FrameSlice slice);
            if (err != FrameParseError.None)
            {
                break;
            }

            if (TryDispatchBroadcastSlice(in slice))
            {
                dispatched++;
            }

            cursor = cursor.Slice(slice.ConsumedBytes);
        }

        return dispatched;
    }

    /// <summary>
    /// Reads once from the underlying TCP stream and dispatches any buffered <see cref="RpcFrameFlags.Broadcast"/> frames.
    /// </summary>
    public async ValueTask<int> ReceiveBroadcastsOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null && _bufferedBytes == 0)
        {
            await FillReceiveBufferAsync(cancellationToken);
        }

        return DrainBufferedBroadcastFrames();
    }

    /// <summary>
    /// Invokes a unary RPC method identified by <paramref name="methodId"/> with <paramref name="request"/> and deserializes <typeparamref name="TResponse"/>.
    /// </summary>
    public ValueTask<TResponse> CallAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        CallAsync<TRequest, TResponse>(new MethodId(methodId), request, cancellationToken);

    /// <summary>
    /// Invokes a parameterless unary RPC method identified by <paramref name="methodId"/> and deserializes <typeparamref name="TResponse"/>.
    /// </summary>
    public ValueTask<TResponse> CallAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        int methodId,
        CancellationToken cancellationToken = default) =>
        CallAsync<TResponse>(new MethodId(methodId), cancellationToken);

    /// <summary>
    /// Invokes a unary RPC method identified by <paramref name="methodId"/> with <paramref name="request"/> and deserializes <typeparamref name="TResponse"/>.
    /// </summary>
    public ValueTask<TResponse> CallAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sendWriter.Clear();
        PooledBufferWriter writer = _sendWriter;
        FrameParser.WriteMemoryPackFrame(ref writer, methodId, RpcFrameFlags.None, in request);
        return SendAndDeserializeAsync<TResponse>(methodId, cancellationToken);
    }

    /// <summary>
    /// Invokes a parameterless unary RPC method identified by <paramref name="methodId"/> and deserializes <typeparamref name="TResponse"/>.
    /// </summary>
    public ValueTask<TResponse> CallAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sendWriter.Clear();
        PooledBufferWriter writer = _sendWriter;
        FrameParser.WriteFrame(ref writer, methodId, RpcFrameFlags.None, ReadOnlySpan<byte>.Empty);
        return SendAndDeserializeAsync<TResponse>(methodId, cancellationToken);
    }

    /// <summary>
    /// Invokes a void/unit unary RPC method identified by <paramref name="methodId"/> with <paramref name="request"/>.
    /// </summary>
    public ValueTask CallVoidAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRequest>(
        MethodId methodId,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sendWriter.Clear();
        PooledBufferWriter writer = _sendWriter;
        FrameParser.WriteMemoryPackFrame(ref writer, methodId, RpcFrameFlags.None, in request);
        return SendAndAwaitAckAsync(methodId, cancellationToken);
    }

    /// <summary>
    /// Invokes a parameterless void/unit unary RPC method identified by <paramref name="methodId"/>.
    /// </summary>
    public ValueTask CallVoidAsync(
        MethodId methodId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sendWriter.Clear();
        PooledBufferWriter writer = _sendWriter;
        FrameParser.WriteFrame(ref writer, methodId, RpcFrameFlags.None, ReadOnlySpan<byte>.Empty);
        return SendAndAwaitAckAsync(methodId, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _broadcastHandlers.Clear();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _sendWriter.Dispose();
        _inMemoryResponseWriter.Dispose();
    }

    private void UnsubscribeBroadcast(MethodId methodId, Action<ReadOnlyMemory<byte>> handler)
    {
        if (!_broadcastHandlers.TryGetValue(methodId, out Action<ReadOnlyMemory<byte>>? current))
        {
            return;
        }

        Action<ReadOnlyMemory<byte>>? updated = current - handler;
        if (updated is null)
        {
            _broadcastHandlers.Remove(methodId);
        }
        else
        {
            _broadcastHandlers[methodId] = updated;
        }
    }

    private bool TryDispatchBroadcastSlice(in FrameSlice slice)
    {
        if ((slice.Header.Flags & RpcFrameFlags.Broadcast) == 0)
        {
            return false;
        }

        if (_broadcastHandlers.TryGetValue(slice.Header.MethodId, out Action<ReadOnlyMemory<byte>>? handler))
        {
            handler(slice.Payload.ToArray());
            return true;
        }

        return false;
    }

    private int DrainBufferedBroadcastFrames()
    {
        int count = 0;
        while (_bufferedBytes > 0)
        {
            FrameParseError err = FrameParser.TryParse(_receiveBuffer.AsSpan(0, _bufferedBytes), RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
            if (err != FrameParseError.None || (frame.Header.Flags & RpcFrameFlags.Broadcast) == 0)
            {
                break;
            }

            _ = TryDispatchBroadcastSlice(in frame);
            ConsumeBufferedBytes(frame.ConsumedBytes);
            count++;
        }

        return count;
    }

    private async ValueTask<TResponse> SendAndDeserializeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        CancellationToken cancellationToken)
    {
        if (_inMemoryDispatcher is not null)
        {
            return await ExecuteInMemoryWithResultAsync<TResponse>(methodId);
        }

        if (_httpClient is not null)
        {
            return await ExecuteHttpWithResultAsync<TResponse>(methodId, cancellationToken);
        }

        await _stream!.WriteAsync(_sendWriter.WrittenMemory, cancellationToken);
        return await ReadAndDeserializeStreamResponseAsync<TResponse>(methodId, cancellationToken);
    }

    private async ValueTask SendAndAwaitAckAsync(MethodId methodId, CancellationToken cancellationToken)
    {
        if (_inMemoryDispatcher is not null)
        {
            await ExecuteInMemoryVoidAsync(methodId);
            return;
        }

        if (_httpClient is not null)
        {
            await ExecuteHttpVoidAsync(methodId, cancellationToken);
            return;
        }

        await _stream!.WriteAsync(_sendWriter.WrittenMemory, cancellationToken);
        await ReadStreamVoidAckAsync(methodId, cancellationToken);
    }

    private async ValueTask<TResponse> ExecuteHttpWithResultAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        CancellationToken cancellationToken)
    {
        byte[] responseBytes = await PostHttpRpcFrameAsync(cancellationToken);
        FrameParseError err = FrameParser.TryParse(responseBytes, RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        return MemoryPackSerializer.Deserialize<TResponse>(frame.Payload)!;
    }

    private async ValueTask ExecuteHttpVoidAsync(MethodId methodId, CancellationToken cancellationToken)
    {
        byte[] responseBytes = await PostHttpRpcFrameAsync(cancellationToken);
        FrameParseError err = FrameParser.TryParse(responseBytes, RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        ValidateResponseFrame(err, frame.Header.Flags, methodId);
    }

    private async ValueTask<byte[]> PostHttpRpcFrameAsync(CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(_sendWriter.WrittenMemory);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");
        using HttpResponseMessage response = await _httpClient!.PostAsync(new Uri(_httpEndpoint, UriKind.RelativeOrAbsolute), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async ValueTask<TResponse> ExecuteInMemoryWithResultAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId)
    {
        await ExecuteInMemoryVoidAsync(methodId);
        FrameParseError err = FrameParser.TryParse(_inMemoryResponseWriter.WrittenSpan, RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        return MemoryPackSerializer.Deserialize<TResponse>(frame.Payload)!;
    }

    private async ValueTask ExecuteInMemoryVoidAsync(MethodId methodId)
    {
        _inMemoryContext!.Bind(new ConnectionId(1UL), RoomId.Invalid, TickNumber.Zero);
        ReadOnlyMemory<byte> payload = _sendWriter.WrittenMemory.Slice(RpcHeader.Size);
        bool handled = await _inMemoryDispatcher!.DispatchAsync(_inMemoryContext, methodId, payload, _inMemoryResponseWriter);
        if (!handled)
        {
            throw new InvalidOperationException($"Unhandled RPC MethodId: {methodId}");
        }
    }

    private async ValueTask<TResponse> ReadAndDeserializeStreamResponseAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryDeserializeNextBufferedFrame<TResponse>(methodId, out TResponse? response))
            {
                return response!;
            }

            await FillReceiveBufferAsync(cancellationToken);
        }
    }

    private async ValueTask ReadStreamVoidAckAsync(MethodId methodId, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryConsumeNextVoidAckFrame(methodId))
            {
                return;
            }

            await FillReceiveBufferAsync(cancellationToken);
        }
    }

    private bool TryDeserializeNextBufferedFrame<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResponse>(
        MethodId methodId,
        out TResponse? response)
    {
        response = default;
        _ = DrainBufferedBroadcastFrames();
        FrameParseError err = FrameParser.TryParse(_receiveBuffer.AsSpan(0, _bufferedBytes), RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err == FrameParseError.IncompleteData)
        {
            return false;
        }

        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        response = MemoryPackSerializer.Deserialize<TResponse>(frame.Payload);
        ConsumeBufferedBytes(frame.ConsumedBytes);
        return true;
    }

    private bool TryConsumeNextVoidAckFrame(MethodId methodId)
    {
        _ = DrainBufferedBroadcastFrames();
        FrameParseError err = FrameParser.TryParse(_receiveBuffer.AsSpan(0, _bufferedBytes), RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err == FrameParseError.IncompleteData)
        {
            return false;
        }

        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        ConsumeBufferedBytes(frame.ConsumedBytes);
        return true;
    }

    private static void ValidateResponseFrame(FrameParseError error, RpcFrameFlags flags, MethodId methodId)
    {
        if (error != FrameParseError.None)
        {
            throw new InvalidOperationException($"RPC frame parse error '{error}' for {methodId}.");
        }

        if ((flags & RpcFrameFlags.Error) != 0)
        {
            throw new InvalidOperationException($"Server returned an RPC error frame for {methodId}.");
        }
    }

    private async ValueTask FillReceiveBufferAsync(CancellationToken cancellationToken)
    {
        int read = await _stream!.ReadAsync(_receiveBuffer.AsMemory(_bufferedBytes), cancellationToken);
        if (read <= 0)
        {
            throw new InvalidOperationException("Remote Hayabusa server closed the RPC connection.");
        }

        _bufferedBytes += read;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConsumeBufferedBytes(int count)
    {
        int remaining = _bufferedBytes - count;
        if (remaining > 0)
        {
            _receiveBuffer.AsSpan(count, remaining).CopyTo(_receiveBuffer);
        }

        _bufferedBytes = remaining;
    }

    private sealed class BroadcastSubscription : IDisposable
    {
        private readonly HayabusaRpcClient _owner;
        private readonly MethodId _methodId;
        private Action<ReadOnlyMemory<byte>>? _handler;

        public BroadcastSubscription(HayabusaRpcClient owner, MethodId methodId, Action<ReadOnlyMemory<byte>> handler)
        {
            _owner = owner;
            _methodId = methodId;
            _handler = handler;
        }

        public void Dispose()
        {
            Action<ReadOnlyMemory<byte>>? h = Interlocked.Exchange(ref _handler, null);
            if (h is not null)
            {
                _owner.UnsubscribeBroadcast(_methodId, h);
            }
        }
    }
}
