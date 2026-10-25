using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a strongly-typed connection identifier with zero runtime overhead over an 8-byte <see cref="ulong"/>.
/// Thread-safe and immutable.
/// </summary>
[MemoryPackable]
public readonly partial record struct ConnectionId(ulong Value) : IComparable<ConnectionId>
{
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
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"conn:{Value}");
}
