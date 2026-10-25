using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed game room (actor) identifier with the same layout as an 8-byte <see cref="ulong"/>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct RoomId(ulong Value) : IComparable<RoomId>
{
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
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"room:{Value}");
}
