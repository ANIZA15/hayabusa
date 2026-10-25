using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a zero-based worker thread (CPU core) index in the Thread-per-Core runtime.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct CoreIndex(int Value) : IComparable<CoreIndex>
{
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
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"core:{Value}");
}
