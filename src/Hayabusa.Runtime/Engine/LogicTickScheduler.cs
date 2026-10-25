using System;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Runtime.Engine;

/// <summary>
/// Deterministic LogicLooper-style fixed-timestep tick scheduler integrated into the worker I/O loop.
/// Computes the exact deadline until the next tick for Phase 4 I/O Wait and prevents the "Spiral of Death"
/// when a tick exceeds its frame budget by clamping catch-up ticks to <see cref="MaxCatchUpTicks"/>.
/// </summary>
public sealed class LogicTickScheduler
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tickInterval;
    private readonly int _maxCatchUpTicks;
    private long _nextTickTimestamp;
    private TickNumber _currentTick;
    private long _skippedTickCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogicTickScheduler"/> class.
    /// </summary>
    /// <param name="tickRateHz">Target tick frequency in Hz (e.g., 60 for 60fps, 30 for 30fps).</param>
    /// <param name="timeProvider">Time provider (supports <c>FakeTimeProvider</c> for deterministic testing).</param>
    /// <param name="maxCatchUpTicks">Maximum number of ticks executed in a single loop turn before skipping ahead to prevent Spiral of Death.</param>
    public LogicTickScheduler(int tickRateHz = 60, TimeProvider? timeProvider = null, int maxCatchUpTicks = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCatchUpTicks);

        TickRateHz = tickRateHz;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tickInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / tickRateHz);
        _maxCatchUpTicks = maxCatchUpTicks;
        _currentTick = TickNumber.Zero;
        ResetSchedule();
    }

    /// <summary>Gets the target tick rate in Hz.</summary>
    public int TickRateHz { get; }

    /// <summary>Gets the duration of a single tick interval.</summary>
    public TimeSpan TickInterval => _tickInterval;

    /// <summary>Gets the maximum number of catch-up ticks allowed per iteration.</summary>
    public int MaxCatchUpTicks => _maxCatchUpTicks;

    /// <summary>Gets the current tick number.</summary>
    public TickNumber CurrentTick => _currentTick;

    /// <summary>Gets the total number of ticks skipped due to frame budget overruns (Spiral of Death protection).</summary>
    public long SkippedTickCount => _skippedTickCount;

    /// <summary>Resets the next scheduled tick deadline relative to the current timestamp.</summary>
    public void ResetSchedule()
    {
        long now = _timeProvider.GetTimestamp();
        _nextTickTimestamp = now + TimestampDeltaFromTimeSpan(_tickInterval);
    }

    /// <summary>
    /// Evaluates how many scheduled ticks are due at the current timestamp and advances the schedule.
    /// If lag exceeds <see cref="MaxCatchUpTicks"/>, skips excess ticks to prevent Spiral of Death.
    /// </summary>
    /// <param name="dueTicks">Buffer to receive the sequential <see cref="TickNumber"/>s to execute now.</param>
    /// <returns>The number of due ticks written into <paramref name="dueTicks"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CollectDueTicks(Span<TickNumber> dueTicks)
    {
        long now = _timeProvider.GetTimestamp();
        long stepDelta = TimestampDeltaFromTimeSpan(_tickInterval);
        if (now < _nextTickTimestamp || dueTicks.IsEmpty)
        {
            return 0;
        }

        int maxToCollect = Math.Min(dueTicks.Length, _maxCatchUpTicks);
        int collected = 0;

        while (now >= _nextTickTimestamp && collected < maxToCollect)
        {
            _currentTick = _currentTick.Next();
            dueTicks[collected++] = _currentTick;
            _nextTickTimestamp += stepDelta;
        }

        SkipExcessLagIfNeeded(now, stepDelta);
        return collected;
    }

    /// <summary>
    /// Computes the remaining time until the next scheduled tick deadline for Phase 4 I/O Wait.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan GetTimeUntilNextTick()
    {
        long now = _timeProvider.GetTimestamp();
        if (now >= _nextTickTimestamp)
        {
            return TimeSpan.Zero;
        }

        long remainingTimestampTicks = _nextTickTimestamp - now;
        long timeSpanTicks = (long)((double)remainingTimestampTicks * TimeSpan.TicksPerSecond / _timeProvider.TimestampFrequency);
        return TimeSpan.FromTicks(Math.Max(0L, timeSpanTicks));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipExcessLagIfNeeded(long now, long stepDelta)
    {
        if (now < _nextTickTimestamp)
        {
            return;
        }

        long overdueDelta = now - _nextTickTimestamp;
        long ticksToSkip = (overdueDelta / stepDelta) + 1;
        _skippedTickCount += ticksToSkip;
        _nextTickTimestamp += ticksToSkip * stepDelta;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long TimestampDeltaFromTimeSpan(TimeSpan span)
    {
        return Math.Max(1L, (long)((double)span.Ticks * _timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));
    }
}
