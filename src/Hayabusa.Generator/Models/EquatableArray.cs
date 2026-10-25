using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Hayabusa.Generator.Models;

/// <summary>
/// Value-equatable immutable array wrapper required for Roslyn <c>IIncrementalGenerator</c> pipeline caching.
/// Prevents IDE cache misses caused by reference-equality collection comparisons.
/// </summary>
/// <typeparam name="TItem">The value-equatable element type.</typeparam>
public readonly struct EquatableArray<TItem> : IEquatable<EquatableArray<TItem>>
    where TItem : IEquatable<TItem>
{
    /// <summary>Gets an empty <see cref="EquatableArray{TItem}"/>.</summary>
    public static readonly EquatableArray<TItem> Empty = new(ImmutableArray<TItem>.Empty);

    private readonly ImmutableArray<TItem> _items;

    /// <summary>
    /// Initializes a new instance of the <see cref="EquatableArray{TItem}"/> struct.
    /// </summary>
    public EquatableArray(ImmutableArray<TItem> items)
    {
        _items = items.IsDefault ? ImmutableArray<TItem>.Empty : items;
    }

    /// <summary>Gets the underlying <see cref="ImmutableArray{T}"/>.</summary>
    public ImmutableArray<TItem> AsImmutableArray() =>
        _items.IsDefault ? ImmutableArray<TItem>.Empty : _items;

    /// <summary>Gets the number of elements in the array.</summary>
    public int Length => _items.IsDefault ? 0 : _items.Length;

    /// <summary>Gets the element at the specified index.</summary>
    public TItem this[int index] => _items[index];

    /// <inheritdoc />
    public bool Equals(EquatableArray<TItem> other)
    {
        ImmutableArray<TItem> self = AsImmutableArray();
        ImmutableArray<TItem> that = other.AsImmutableArray();
        if (self.Length != that.Length)
        {
            return false;
        }

        for (int i = 0; i < self.Length; i++)
        {
            if (!EqualityComparer<TItem>.Default.Equals(self[i], that[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<TItem> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        ImmutableArray<TItem> self = AsImmutableArray();
        int hash = 17;
        for (int i = 0; i < self.Length; i++)
        {
            hash = unchecked((hash * 31) + EqualityComparer<TItem>.Default.GetHashCode(self[i]));
        }

        return hash;
    }

    /// <summary>Checks value equality between two <see cref="EquatableArray{TItem}"/> instances.</summary>
    public static bool operator ==(EquatableArray<TItem> left, EquatableArray<TItem> right) => left.Equals(right);

    /// <summary>Checks value inequality between two <see cref="EquatableArray{TItem}"/> instances.</summary>
    public static bool operator !=(EquatableArray<TItem> left, EquatableArray<TItem> right) => !left.Equals(right);
}
