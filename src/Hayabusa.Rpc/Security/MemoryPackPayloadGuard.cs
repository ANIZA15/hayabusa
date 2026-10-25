using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Hayabusa.Rpc.Security;

/// <summary>
/// Guards against MemoryPack Collection Length Bomb attacks (e.g., a small 8-byte payload claiming 100,000,000 elements)
/// prior to invoking <see cref="MemoryPackSerializer.Deserialize{T}(ReadOnlySpan{byte}, ref T, MemoryPackSerializerOptions?)"/>.
/// </summary>
public static class MemoryPackPayloadGuard
{
    /// <summary>Default maximum collection/string element count permitted in a single RPC payload.</summary>
    public const int DefaultMaxElementCount = 4096;

    /// <summary>
    /// Validates that a MemoryPack collection/string header at the start of <paramref name="payload"/>
    /// does not declare more elements than can physically exist in the remaining payload bytes or exceed <paramref name="maxElementCount"/>.
    /// </summary>
    /// <param name="payload">Raw MemoryPack payload span.</param>
    /// <param name="minElementByteSize">Minimum possible wire size of a single element (minimum 1 byte).</param>
    /// <param name="maxElementCount">Maximum allowed collection count.</param>
    /// <returns><c>true</c> if the collection header is safe; <c>false</c> if a length bomb is detected.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ValidateCollectionHeader(
        scoped ReadOnlySpan<byte> payload,
        int minElementByteSize = 1,
        int maxElementCount = DefaultMaxElementCount)
    {
        if (payload.Length < 4)
        {
            return false;
        }

        int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        if (declaredCount == -1)
        {
            // MemoryPack null collection marker (-1) is safe and allocates nothing.
            return true;
        }

        if (declaredCount < 0 || declaredCount > maxElementCount)
        {
            return false;
        }

        int remainingBytes = payload.Length - 4;
        long requiredMinBytes = (long)declaredCount * Math.Max(1, minElementByteSize);
        return requiredMinBytes <= remainingBytes;
    }

    /// <summary>
    /// Safely deserializes a MemoryPack value of type <typeparamref name="T"/>, catching corrupted payload exceptions
    /// without crashing the worker thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDeserializeSafe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        scoped ReadOnlySpan<byte> payload,
        out T? value,
        bool isCollectionOrStringRoot = false,
        int maxElementCount = DefaultMaxElementCount)
    {
        value = default;
        if (isCollectionOrStringRoot && !ValidateCollectionHeader(payload, 1, maxElementCount))
        {
            return false;
        }

        try
        {
            value = MemoryPackSerializer.Deserialize<T>(payload);
            return true;
        }
        catch (MemoryPackSerializationException)
        {
            value = default;
            return false;
        }
    }
}
