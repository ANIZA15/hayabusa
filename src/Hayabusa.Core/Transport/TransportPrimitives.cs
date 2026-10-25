using System;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Core.Transport;

/// <summary>
/// Specifies the kind of asynchronous I/O operation in the completion-based transport model (<c>io_uring</c> / <c>IOCP</c>).
/// </summary>
public enum TransportOperationKind : byte
{
    /// <summary>No operation.</summary>
    None = 0,

    /// <summary>Accept a new incoming client connection.</summary>
    Accept = 1,

    /// <summary>Receive data from a connection into a pinned slab slot.</summary>
    Receive = 2,

    /// <summary>Send data from a pinned buffer to a connection.</summary>
    Send = 3,

    /// <summary>Close and release a connection.</summary>
    Disconnect = 4,
}

/// <summary>
/// Identifies a specific slot and generation token within a <see cref="Buffers.PinnedSlabPool"/>.
/// Thread-safe and immutable value type.
/// </summary>
/// <param name="SlotIndex">Index of the pinned slab slot, or -1 if none.</param>
/// <param name="Generation">Generation token of the pinned slab slot.</param>
public readonly record struct SlabSlotToken(int SlotIndex, uint Generation)
{
    /// <summary>Gets an empty token representing no slab slot.</summary>
    public static readonly SlabSlotToken None = new(-1, 0);

    /// <summary>Gets a value indicating whether this token refers to a valid slab slot.</summary>
    public bool HasSlot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => SlotIndex >= 0 && Generation > 0;
    }
}

/// <summary>
/// Represents a Completion Queue Entry (CQE) produced by the kernel or transport backend when an I/O operation finishes.
/// Thread-safe and immutable value type.
/// </summary>
/// <param name="Operation">The completed operation kind.</param>
/// <param name="ConnectionId">The target connection identifier.</param>
/// <param name="BytesTransferred">Number of bytes transferred (or 0 on EOF, negative on error).</param>
/// <param name="SlabToken">Pinned slab slot token holding received/sent payload.</param>
public readonly record struct TransportCompletionEvent(
    TransportOperationKind Operation,
    ConnectionId ConnectionId,
    int BytesTransferred,
    SlabSlotToken SlabToken)
{
    /// <summary>Gets the pinned slab slot index.</summary>
    public int SlabSlotIndex => SlabToken.SlotIndex;

    /// <summary>Gets the pinned slab generation token.</summary>
    public uint SlabGeneration => SlabToken.Generation;

    /// <summary>Gets a value indicating whether the completion represents a graceful disconnect or error.</summary>
    public bool IsClosedOrError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Operation == TransportOperationKind.Disconnect ||
               (Operation == TransportOperationKind.Receive && BytesTransferred <= 0);
    }
}

/// <summary>
/// Represents a Submission Queue Entry (SQE) queued locally on a worker thread before a single batched flush syscall.
/// Thread-safe and immutable value type.
/// </summary>
/// <param name="Operation">The requested I/O operation kind.</param>
/// <param name="ConnectionId">The target connection identifier.</param>
/// <param name="Payload">Read-only memory slice to transmit (for <see cref="TransportOperationKind.Send"/>).</param>
/// <param name="SlabToken">Optional pinned slab slot token to return or register upon completion.</param>
public readonly record struct TransportSubmissionEntry(
    TransportOperationKind Operation,
    ConnectionId ConnectionId,
    ReadOnlyMemory<byte> Payload,
    SlabSlotToken SlabToken = default);
