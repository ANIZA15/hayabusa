using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Runtime.Threading;

/// <summary>
/// Custom <see cref="SynchronizationContext"/> pinned to a single dedicated worker thread (<see cref="CoreIndex"/>).
/// Ensures that <c>ValueTask</c> and <c>UniTask</c> continuations never escape to the global <see cref="ThreadPool"/>,
/// preserving CPU cache locality and lock-free room state invariants.
/// </summary>
public sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly record struct WorkItem(SendOrPostCallback Callback, object? State);

    private readonly ConcurrentQueue<WorkItem> _workQueue = new();
    private readonly int _ownerManagedThreadId;

    /// <summary>
    /// Initializes a new instance of the <see cref="SingleThreadSynchronizationContext"/> class.
    /// </summary>
    /// <param name="coreIndex">The core index bound to this synchronization context.</param>
    /// <param name="ownerManagedThreadId">The managed thread ID of the dedicated worker thread.</param>
    public SingleThreadSynchronizationContext(CoreIndex coreIndex, int ownerManagedThreadId)
    {
        CoreIndex = coreIndex;
        _ownerManagedThreadId = ownerManagedThreadId;
    }

    /// <summary>Gets the worker core index owning this synchronization context.</summary>
    public CoreIndex CoreIndex { get; }

    /// <summary>Gets a value indicating whether the current thread is the dedicated owner thread.</summary>
    public bool IsOnOwnerThread
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Environment.CurrentManagedThreadId == _ownerManagedThreadId;
    }

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _workQueue.Enqueue(new WorkItem(d, state));
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (IsOnOwnerThread)
        {
            d(state);
            return;
        }

        _workQueue.Enqueue(new WorkItem(d, state));
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Drains and executes up to <paramref name="maxItems"/> scheduled continuations on the owner worker thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainContinuations(int maxItems = 256)
    {
        int executed = 0;
        while (executed < maxItems && _workQueue.TryDequeue(out WorkItem item))
        {
            item.Callback(item.State);
            executed++;
        }

        return executed;
    }
}
