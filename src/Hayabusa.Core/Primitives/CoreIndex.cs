using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cysharp.Text;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a zero-based worker thread (CPU core) index in the Thread-per-Core runtime.
/// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/> for zero-allocation formatting with Cysharp <c>ZString</c> and <c>ZLogger</c>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct CoreIndex(int Value) : IComparable<CoreIndex>, ISpanFormattable, IUtf8SpanFormattable
{
    private static ReadOnlySpan<byte> Utf8Prefix => "core:"u8;

    /// <summary>Gets the primary core index (0).</summary>
    public static readonly CoreIndex Zero = new(0);

    /// <summary>Gets a value indicating whether this core index is non-negative.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Value >= 0;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(CoreIndex other) => Value.CompareTo(other.Value);

    /// <summary>Determines whether one <see cref="CoreIndex"/> precedes another.</summary>
    public static bool operator <(CoreIndex left, CoreIndex right) => left.Value < right.Value;

    /// <summary>Determines whether one <see cref="CoreIndex"/> precedes or equals another.</summary>
    public static bool operator <=(CoreIndex left, CoreIndex right) => left.Value <= right.Value;

    /// <summary>Determines whether one <see cref="CoreIndex"/> follows another.</summary>
    public static bool operator >(CoreIndex left, CoreIndex right) => left.Value > right.Value;

    /// <summary>Determines whether one <see cref="CoreIndex"/> follows or equals another.</summary>
    public static bool operator >=(CoreIndex left, CoreIndex right) => left.Value >= right.Value;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!"core:".AsSpan().TryCopyTo(destination) ||
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
    public override string ToString() => ZString.Concat("core:", Value);
}
