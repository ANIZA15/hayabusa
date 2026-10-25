using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Rpc.Framing;

/// <summary>
/// Enumeration of all possible frame parsing outcomes.
/// </summary>
public enum FrameParseError : byte
{
    /// <summary>Frame was parsed completely and is valid.</summary>
    None = 0,

    /// <summary>More bytes are required from the transport to complete the header or payload.</summary>
    IncompleteData = 1,

    /// <summary>The leading 2-byte magic signature (<c>0x48, 0x59</c>) did not match.</summary>
    InvalidMagicByte = 2,

    /// <summary>The declared payload length is negative or exceeds the configured <c>MaxFrameSize</c>.</summary>
    PayloadTooLarge = 3,

    /// <summary>The protocol version byte is not supported by this server.</summary>
    UnsupportedProtocolVersion = 4,
}

/// <summary>
/// Flags describing the semantics of an <see cref="RpcHeader"/>.
/// </summary>
[Flags]
public enum RpcFrameFlags : byte
{
    /// <summary>Standard unary RPC request frame.</summary>
    None = 0,

    /// <summary>RPC response frame.</summary>
    Response = 1 << 0,

    /// <summary>RPC error frame.</summary>
    Error = 1 << 1,

    /// <summary>Server-to-client room tick broadcast frame.</summary>
    Broadcast = 1 << 2,

    /// <summary>One-way fire-and-forget notification frame.</summary>
    FireAndForget = 1 << 3,
}

/// <summary>
/// Fixed 12-byte wire header for Hayabusa binary RPC frames.
/// Wire layout:
/// [0..1]  Magic ('H' = 0x48, 'Y' = 0x59)
/// [2..5]  PayloadLength (int32 Little Endian)
/// [6]     Version (0x01)
/// [7]     Flags (RpcFrameFlags)
/// [8..11] MethodId (int32 Little Endian)
/// </summary>
/// <param name="PayloadLength">Length in bytes of the body following the 12-byte header.</param>
/// <param name="Version">Protocol version byte (currently 1).</param>
/// <param name="Flags">Frame flags.</param>
/// <param name="MethodId">Target RPC method identifier.</param>
public readonly record struct RpcHeader(
    int PayloadLength,
    byte Version,
    RpcFrameFlags Flags,
    MethodId MethodId)
{
    /// <summary>First magic byte ('H' = 0x48).</summary>
    public const byte MagicByte0 = 0x48;

    /// <summary>Second magic byte ('Y' = 0x59).</summary>
    public const byte MagicByte1 = 0x59;

    /// <summary>Current supported wire protocol version (1).</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Total fixed size in bytes of the wire header (12 bytes).</summary>
    public const int Size = 12;

    /// <summary>Default maximum allowed frame payload size (64 KB).</summary>
    public const int DefaultMaxFrameSize = 64 * 1024;

    /// <summary>Gets the total frame byte length including header and payload.</summary>
    public int TotalFrameLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Size + PayloadLength;
    }

    /// <summary>Writes this 12-byte header into <paramref name="destination"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Destination span must be at least 12 bytes.", nameof(destination));
        }

        destination[0] = MagicByte0;
        destination[1] = MagicByte1;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(2, 4), PayloadLength);
        destination[6] = Version;
        destination[7] = (byte)Flags;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8, 4), MethodId.Value);
    }
}

/// <summary>
/// Stack-only view over a parsed RPC frame within a contiguous receive buffer.
/// </summary>
public readonly ref struct FrameSlice
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FrameSlice"/> struct.
    /// </summary>
    public FrameSlice(RpcHeader header, ReadOnlySpan<byte> payload, int consumedBytes)
    {
        Header = header;
        Payload = payload;
        ConsumedBytes = consumedBytes;
    }

    /// <summary>Gets the parsed 12-byte RPC header.</summary>
    public RpcHeader Header { get; }

    /// <summary>Gets the zero-copy slice of the frame payload.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>Gets the total bytes consumed from the input buffer (Header + Payload).</summary>
    public int ConsumedBytes { get; }
}
