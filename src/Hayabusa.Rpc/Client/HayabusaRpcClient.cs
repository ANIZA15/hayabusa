using System;
using System.Diagnostics.CodeAnalysis;
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
/// High-performance binary MemoryPack RPC client for Hayabusa servers.
/// Supports both raw TCP connections (<see cref="ConnectAsync"/>) and zero-socket in-memory dispatch (<see cref="CreateInMemory"/>).
/// Ownership: Caller must call <see cref="Dispose"/> to close the underlying socket and return pooled buffers.
/// </summary>
public sealed class HayabusaRpcClient : IDisposable
{
    private readonly TcpClient? _tcpClient;
    private readonly NetworkStream? _stream;
    private readonly RpcDispatcher? _inMemoryDispatcher;
    private readonly HayabusaContext? _inMemoryContext;
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
    /// for deterministic unit testing and in-process invocation without opening TCP sockets.
    /// </summary>
    public static HayabusaRpcClient CreateInMemory(RpcDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new HayabusaRpcClient(dispatcher);
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
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _sendWriter.Dispose();
        _inMemoryResponseWriter.Dispose();
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

        await _stream!.WriteAsync(_sendWriter.WrittenMemory, cancellationToken);
        await ReadStreamVoidAckAsync(methodId, cancellationToken);
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
        FrameParseError err = FrameParser.TryParse(_receiveBuffer.AsSpan(0, _bufferedBytes), RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err == FrameParseError.IncompleteData)
        {
            return false;
        }

        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        if ((frame.Header.Flags & RpcFrameFlags.Broadcast) != 0)
        {
            ConsumeBufferedBytes(frame.ConsumedBytes);
            return false;
        }

        response = MemoryPackSerializer.Deserialize<TResponse>(frame.Payload);
        ConsumeBufferedBytes(frame.ConsumedBytes);
        return true;
    }

    private bool TryConsumeNextVoidAckFrame(MethodId methodId)
    {
        FrameParseError err = FrameParser.TryParse(_receiveBuffer.AsSpan(0, _bufferedBytes), RpcHeader.DefaultMaxFrameSize, out FrameSlice frame);
        if (err == FrameParseError.IncompleteData)
        {
            return false;
        }

        ValidateResponseFrame(err, frame.Header.Flags, methodId);
        bool isBroadcast = (frame.Header.Flags & RpcFrameFlags.Broadcast) != 0;
        ConsumeBufferedBytes(frame.ConsumedBytes);
        return !isBroadcast;
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
}
