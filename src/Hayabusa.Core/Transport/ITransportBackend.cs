using System;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Core.Transport;

/// <summary>
/// Defines the completion-based I/O transport abstraction (Linux <c>io_uring</c>, cross-platform <c>IOCP</c>/<c>Socket</c>, or <c>InMemoryTransport</c>).
/// Designed for batch draining of Completion Queue Entries (CQEs) and single-syscall flushing of Submission Queue Entries (SQEs).
/// </summary>
public interface ITransportBackend : IDisposable
{
    /// <summary>Gets the human-readable name of this transport backend (e.g., <c>"io_uring"</c>, <c>"socket"</c>, <c>"inmemory"</c>).</summary>
    string Name { get; }

    /// <summary>Gets the pinned slab pool registered with this worker transport instance.</summary>
    PinnedSlabPool SlabPool { get; }

    /// <summary>
    /// Drains up to <c>destination.Length</c> completed I/O events (CQEs) without blocking.
    /// </summary>
    /// <param name="destination">Pre-allocated buffer to receive completed events.</param>
    /// <returns>The number of completion events written into <paramref name="destination"/>.</returns>
    int DrainCompletions(Span<TransportCompletionEvent> destination);

    /// <summary>
    /// Submits a batch of queued SQEs in a single operation and optionally waits up to <paramref name="timeout"/>
    /// (the next game loop tick deadline) for new completions.
    /// </summary>
    /// <param name="submissions">Batch of submission entries to flush.</param>
    /// <param name="timeout">Maximum duration to wait for I/O if no completions are immediately ready.</param>
    /// <returns>The number of submissions processed.</returns>
    int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout);
}
