using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cysharp.Text;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed connection identifier with zero runtime overhead over an 8-byte <see cref="ulong"/>.
/// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/> for zero-allocation formatting with Cysharp <c>ZString</c> and <c>ZLogger</c>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct ConnectionId(ulong Value) : IComparable<ConnectionId>, ISpanFormattable, IUtf8SpanFormattable
{
    private static ReadOnlySpan<byte> Utf8Prefix => "conn:"u8;

    /// <summary>Gets an uninitialized or invalid connection identifier.</summary>
    public static readonly ConnectionId Invalid = new(0UL);

    /// <summary>Gets a value indicating whether this connection identifier represents an active valid connection.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Value != 0UL;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(ConnectionId other) => Value.CompareTo(other.Value);

    /// <summary>Determines whether one <see cref="ConnectionId"/> precedes another.</summary>
    public static bool operator <(ConnectionId left, ConnectionId right) => left.Value < right.Value;

    /// <summary>Determines whether one <see cref="ConnectionId"/> precedes or equals another.</summary>
    public static bool operator <=(ConnectionId left, ConnectionId right) => left.Value <= right.Value;

    /// <summary>Determines whether one <see cref="ConnectionId"/> follows another.</summary>
    public static bool operator >(ConnectionId left, ConnectionId right) => left.Value > right.Value;

    /// <summary>Determines whether one <see cref="ConnectionId"/> follows or equals another.</summary>
    public static bool operator >=(ConnectionId left, ConnectionId right) => left.Value >= right.Value;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!"conn:".AsSpan().TryCopyTo(destination) ||
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
    public override string ToString() => ZString.Concat("conn:", Value);
}
