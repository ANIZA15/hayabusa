using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cysharp.Text;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a monotonically increasing frame/tick number in the deterministic game loop.
/// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/> for zero-allocation formatting with Cysharp <c>ZString</c> and <c>ZLogger</c>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct TickNumber(ulong Value) : IComparable<TickNumber>, ISpanFormattable, IUtf8SpanFormattable
{
    private static ReadOnlySpan<byte> Utf8Prefix => "tick:"u8;

    /// <summary>Gets the initial tick number (0).</summary>
    public static readonly TickNumber Zero = new(0UL);

    /// <summary>Returns the next sequential tick number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TickNumber Next() => new(Value + 1UL);

    /// <summary>Returns a tick number advanced by the specified delta.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TickNumber Advance(ulong delta) => new(Value + delta);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(TickNumber other) => Value.CompareTo(other.Value);

    /// <summary>Determines whether one <see cref="TickNumber"/> precedes another.</summary>
    public static bool operator <(TickNumber left, TickNumber right) => left.Value < right.Value;

    /// <summary>Determines whether one <see cref="TickNumber"/> precedes or equals another.</summary>
    public static bool operator <=(TickNumber left, TickNumber right) => left.Value <= right.Value;

    /// <summary>Determines whether one <see cref="TickNumber"/> follows another.</summary>
    public static bool operator >(TickNumber left, TickNumber right) => left.Value > right.Value;

    /// <summary>Determines whether one <see cref="TickNumber"/> follows or equals another.</summary>
    public static bool operator >=(TickNumber left, TickNumber right) => left.Value >= right.Value;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!"tick:".AsSpan().TryCopyTo(destination) ||
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
    public override string ToString() => ZString.Concat("tick:", Value);
}
