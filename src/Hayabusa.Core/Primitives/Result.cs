using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Hayabusa.Core.Primitives;

/// <summary>
/// Represents a zero-allocation discriminated union of a success value <typeparamref name="TValue"/>
/// or a typed error enum <typeparamref name="TError"/>.
/// Thread-safe and immutable.
/// </summary>
/// <typeparam name="TValue">The success value type.</typeparam>
/// <typeparam name="TError">The strongly-typed error enum.</typeparam>
public readonly struct Result<TValue, TError> : IEquatable<Result<TValue, TError>>
    where TError : struct, Enum
{
    private readonly TValue? _value;
    private readonly TError _error;
    private readonly bool _isSuccess;

    private Result(TValue value)
    {
        _value = value;
        _error = default;
        _isSuccess = true;
    }

    private Result(TError error)
    {
        _value = default;
        _error = error;
        _isSuccess = false;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isSuccess;
    }

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_isSuccess;
    }

    /// <summary>Gets the error code when <see cref="IsFailure"/> is true, or default when successful.</summary>
    public TError Error
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _error;
    }

    /// <summary>Gets the underlying value if successful; throws <see cref="InvalidOperationException"/> otherwise.</summary>
    public TValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isSuccess ? _value! : ThrowFailed(_error);
    }

    /// <summary>Creates a successful result containing <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Result<TValue, TError> Success(TValue value) => new(value);

    /// <summary>Creates a failed result with <paramref name="error"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Result<TValue, TError> Failure(TError error) => new(error);

    /// <summary>Attempts to retrieve the success value without throwing exceptions.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue([MaybeNullWhen(false)] out TValue value, out TError error)
    {
        value = _value;
        error = _error;
        return _isSuccess;
    }

    /// <inheritdoc />
    public bool Equals(Result<TValue, TError> other)
    {
        if (_isSuccess != other._isSuccess)
        {
            return false;
        }

        return _isSuccess
            ? EqualityComparer<TValue?>.Default.Equals(_value, other._value)
            : EqualityComparer<TError>.Default.Equals(_error, other._error);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<TValue, TError> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _isSuccess
        ? HashCode.Combine(true, _value)
        : HashCode.Combine(false, _error);

    /// <summary>Checks equality between two results.</summary>
    public static bool operator ==(Result<TValue, TError> left, Result<TValue, TError> right) => left.Equals(right);

    /// <summary>Checks inequality between two results.</summary>
    public static bool operator !=(Result<TValue, TError> left, Result<TValue, TError> right) => !left.Equals(right);

    [DoesNotReturn]
    private static TValue ThrowFailed(TError error) =>
        throw new InvalidOperationException($"Cannot access Value on a failed Result (Error: {error}).");
}
