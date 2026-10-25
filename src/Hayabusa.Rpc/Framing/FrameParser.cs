using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;
using MemoryPack;

namespace Hayabusa.Rpc.Framing;

/// <summary>
/// Zero-allocation parser and writer for Hayabusa 12-byte binary RPC frames.
/// Defends against Length-Prefix OOM attacks prior to any memory allocation.
/// </summary>
public static class FrameParser
{
    private const int MagicAndLengthPrefixSize = 6;

    [ThreadStatic]
    private static ScratchArrayBufferWriter? t_scratchWriter;

    /// <summary>
    /// Attempts to parse a single RPC frame from <paramref name="input"/> without any heap allocations.
    /// Immediately rejects invalid magic bytes or out-of-range length prefixes even on partial headers.
    /// </summary>
    /// <param name="input">Contiguous input byte span.</param>
    /// <param name="maxFrameSize">Maximum permitted payload size in bytes.</param>
    /// <param name="frame">Parsed frame slice when return value is <see cref="FrameParseError.None"/>.</param>
    /// <returns>A <see cref="FrameParseError"/> indicating success or the exact rejection/incomplete reason.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FrameParseError TryParse(
        ReadOnlySpan<byte> input,
        int maxFrameSize,
        out FrameSlice frame)
    {
        frame = default;
        if (input.Length < 2)
        {
            return FrameParseError.IncompleteData;
        }

        if (!HasValidMagic(input))
        {
            return FrameParseError.InvalidMagicByte;
        }

        if (input.Length < MagicAndLengthPrefixSize)
        {
            return FrameParseError.IncompleteData;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(2, 4));
        if ((uint)payloadLength > (uint)maxFrameSize)
        {
            return FrameParseError.PayloadTooLarge;
        }

        return TryParseBody(input, payloadLength, out frame);
    }

    /// <summary>
    /// Writes a complete RPC frame (12-byte header + raw payload bytes) directly into <paramref name="writer"/>.
    /// Supports <c>allows ref struct</c> (C# 13) so stack-allocated <c>ref struct</c> buffer writers can be used with zero boxing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFrame<TWriter>(
        ref TWriter writer,
        MethodId methodId,
        RpcFrameFlags flags,
        scoped ReadOnlySpan<byte> payload)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        int totalLength = RpcHeader.Size + payload.Length;
        Span<byte> span = writer.GetSpan(totalLength);
        var header = new RpcHeader(payload.Length, RpcHeader.CurrentVersion, flags, methodId);
        header.WriteTo(span);
        payload.CopyTo(span.Slice(RpcHeader.Size, payload.Length));
        writer.Advance(totalLength);
    }

    /// <summary>
    /// Serializes <paramref name="value"/> using <see cref="MemoryPackSerializer"/> with a 12-byte RPC frame header
    /// with zero heap allocations on warm threads.
    /// Supports <c>allows ref struct</c> (C# 13) for stack-only buffer writers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMemoryPackFrame<TWriter, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TPayload>(
        ref TWriter writer,
        MethodId methodId,
        RpcFrameFlags flags,
        in TPayload value)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        ScratchArrayBufferWriter scratch = GetThreadLocalScratch();
        MemoryPackSerializer.Serialize(in scratch, in value);
        WriteFrame(ref writer, methodId, flags, scratch.WrittenSpan);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ScratchArrayBufferWriter GetThreadLocalScratch()
    {
        ScratchArrayBufferWriter? scratch = t_scratchWriter;
        if (scratch is null)
        {
            scratch = new ScratchArrayBufferWriter(64 * 1024);
            t_scratchWriter = scratch;
        }
        else
        {
            scratch.Reset();
        }

        return scratch;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasValidMagic(scoped ReadOnlySpan<byte> input) =>
        input[0] == RpcHeader.MagicByte0 && input[1] == RpcHeader.MagicByte1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FrameParseError TryParseBody(
        ReadOnlySpan<byte> input,
        int payloadLength,
        out FrameSlice frame)
    {
        frame = default;
        if (input.Length < RpcHeader.Size)
        {
            return FrameParseError.IncompleteData;
        }

        byte version = input[6];
        if (version != RpcHeader.CurrentVersion)
        {
            return FrameParseError.UnsupportedProtocolVersion;
        }

        int totalSize = RpcHeader.Size + payloadLength;
        if (input.Length < totalSize)
        {
            return FrameParseError.IncompleteData;
        }

        var header = new RpcHeader(
            payloadLength,
            version,
            (RpcFrameFlags)input[7],
            new MethodId(BinaryPrimitives.ReadInt32LittleEndian(input.Slice(8, 4))));

        frame = new FrameSlice(header, input.Slice(RpcHeader.Size, payloadLength), totalSize);
        return FrameParseError.None;
    }

    private sealed class ScratchArrayBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer;
        private int _offset;

        public ScratchArrayBufferWriter(int capacity)
        {
            _buffer = GC.AllocateArray<byte>(capacity, pinned: true);
        }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset() => _offset = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count) => _offset += count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int sizeHint)
        {
            int required = sizeHint <= 0 ? 256 : sizeHint;
            if (_buffer.Length - _offset < required)
            {
                int nextSize = Math.Max(_buffer.Length * 2, _offset + required);
                byte[] next = GC.AllocateArray<byte>(nextSize, pinned: true);
                _buffer.AsSpan(0, _offset).CopyTo(next);
                _buffer = next;
            }
        }
    }
}
