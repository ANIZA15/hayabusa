using System;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cysharp.Text;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed RPC method identifier with the same layout as a 4-byte <see cref="int"/>.
/// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/> for zero-allocation formatting with Cysharp <c>ZString</c> and <c>ZLogger</c>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct MethodId(int Value) : IComparable<MethodId>, ISpanFormattable, IUtf8SpanFormattable
{
    private static ReadOnlySpan<byte> Utf8Prefix => "method:"u8;

    /// <summary>Gets the invalid method identifier (0).</summary>
    public static readonly MethodId Invalid = new(0);

    /// <summary>Gets a value indicating whether this method identifier is valid (strictly positive).</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Value > 0;
    }

    /// <summary>
    /// Computes a deterministic positive 31-bit FNV-1a <see cref="MethodId"/> from a service name and method name.
    /// Used when <c>[Rpc]</c> is declared without an explicit integer identifier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MethodId FromRoute(ReadOnlySpan<char> serviceName, ReadOnlySpan<char> methodName)
    {
        uint hash = HashCharsFnv1a(2166136261u, serviceName);
        hash = (hash ^ '.') * 16777619u;
        hash = HashCharsFnv1a(hash, methodName);
        int positive = (int)(hash & 0x7FFFFFFFu);
        return new MethodId(positive == 0 ? 1 : positive);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashCharsFnv1a(uint seed, scoped ReadOnlySpan<char> chars)
    {
        uint hash = seed;
        for (int i = 0; i < chars.Length; i++)
        {
            hash = (hash ^ chars[i]) * 16777619u;
        }

        return hash;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(MethodId other) => Value.CompareTo(other.Value);

    /// <summary>Determines whether one <see cref="MethodId"/> precedes another.</summary>
    public static bool operator <(MethodId left, MethodId right) => left.Value < right.Value;

    /// <summary>Determines whether one <see cref="MethodId"/> precedes or equals another.</summary>
    public static bool operator <=(MethodId left, MethodId right) => left.Value <= right.Value;

    /// <summary>Determines whether one <see cref="MethodId"/> follows another.</summary>
    public static bool operator >(MethodId left, MethodId right) => left.Value > right.Value;

    /// <summary>Determines whether one <see cref="MethodId"/> follows or equals another.</summary>
    public static bool operator >=(MethodId left, MethodId right) => left.Value >= right.Value;

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!"method:".AsSpan().TryCopyTo(destination) ||
            !Value.TryFormat(destination.Slice(7), out int numWritten, format, provider ?? CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = 7 + numWritten;
        return true;
    }

    /// <inheritdoc />
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (!Utf8Prefix.TryCopyTo(utf8Destination) ||
            !Utf8Formatter.TryFormat(Value, utf8Destination.Slice(7), out int numWritten))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = 7 + numWritten;
        return true;
    }

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public override string ToString() => ZString.Concat("method:", Value);
}
