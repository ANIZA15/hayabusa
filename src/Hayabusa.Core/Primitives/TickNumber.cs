using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a monotonically increasing frame/tick number in the deterministic game loop.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct TickNumber(ulong Value) : IComparable<TickNumber>
{
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
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"tick:{Value}");
}
