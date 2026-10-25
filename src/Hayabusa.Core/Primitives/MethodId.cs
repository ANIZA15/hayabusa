using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed RPC method identifier with the same layout as a 4-byte <see cref="int"/>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct MethodId(int Value) : IComparable<MethodId>
{
    /// <summary>Gets the invalid method identifier (0).</summary>
    public static readonly MethodId Invalid = new(0);

    /// <summary>Gets a value indicating whether this method identifier is valid (strictly positive).</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Value > 0;
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
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"method:{Value}");
}
