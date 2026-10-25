using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cysharp.Text;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed game room (actor) identifier with the same layout as an 8-byte <see cref="ulong"/>.
/// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/> for zero-allocation formatting with Cysharp <c>ZString</c> and <c>ZLogger</c>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct RoomId(ulong Value) : IComparable<RoomId>, ISpanFormattable, IUtf8SpanFormattable
{
    private static ReadOnlySpan<byte> Utf8Prefix => "room:"u8;

    /// <summary>Gets an uninitialized or invalid room identifier.</summary>
    public static readonly RoomId Invalid = new(0UL);

    /// <summary>Gets a value indicating whether this room identifier is valid (non-zero).</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Value != 0UL;
    }

    /// <summary>
    /// Computes the deterministic core affinity for this room given a positive worker count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CoreIndex ResolveCoreAffinity(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        return new CoreIndex((int)(Value % (ulong)workerCount));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(RoomId other) => Value.CompareTo(other.Value);

    /// <summary>Determines whether one <see cref="RoomId"/> precedes another.</summary>
    public static bool operator <(RoomId left, RoomId right) => left.Value < right.Value;

    /// <summary>Determines whether one <see cref="RoomId"/> precedes or equals another.</summary>
    public static bool operator <=(RoomId left, RoomId right) => left.Value <= right.Value;

    /// <summary>Determines whether one <see cref="RoomId"/> follows another.</summary>
    public static bool operator >(RoomId left, RoomId right) => left.Value > right.Value;

    /// <summary>Determines whether one <see cref="RoomId"/> follows or equals another.</summary>
    public static bool operator >=(RoomId left, RoomId right) => left.Value >= right.Value;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!"room:".AsSpan().TryCopyTo(destination) ||
            !Value.TryFormat(destination.Slice(5), out int numWritten, format, provider ?? CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = 5 + numWritten;
        return true;
    }

    /// <inheritdoc />
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!Utf8Prefix.TryCopyTo(utf8Destination) ||
            !Utf8Formatter.TryFormat(Value, utf8Destination.Slice(5), out int numWritten))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = 5 + numWritten;
        return true;
    }

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public override string ToString() => ZString.Concat("room:", Value);
}
