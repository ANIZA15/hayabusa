using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Runtime.Timing;

/// <summary>
/// Deterministic connection activity tracker and timeout sweep wheel bound to a single worker thread.
/// Defends against Slowloris (slow-drip partial frames) and idle socket leaks without per-connection timers or <see cref="System.Threading.CancellationTokenSource"/> allocations.
/// </summary>
public sealed class TimingWheel
{
    private struct ConnectionEntry
    {
        public long LastActivityTimestamp;
        public long PartialFrameStartTimestamp;
        public int PartialBytesAccumulated;
        public bool HasPartialFrame;
    }

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _frameReadTimeout;
    private readonly Dictionary<ConnectionId, ConnectionEntry> _entries = new(256);
    private long _lastSweepTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimingWheel"/> class.
    /// </summary>
    /// <param name="timeProvider">Time provider (supports <c>FakeTimeProvider</c> in deterministic unit tests).</param>
    /// <param name="idleTimeout">Maximum idle time before a connection is evicted.</param>
    /// <param name="frameReadTimeout">Maximum time allowed to complete a fragmented frame (Slowloris defense).</param>
    public TimingWheel(
        TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? frameReadTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _frameReadTimeout = frameReadTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Gets the number of active tracked connections.</summary>
    public int TrackedCount => _entries.Count;

    /// <summary>Registers or refreshes a connection's activity timestamp upon accept or complete frame processing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TouchCompleteFrame(ConnectionId connectionId)
    {
        long now = _timeProvider.GetTimestamp();
        _entries[connectionId] = new ConnectionEntry
        {
            LastActivityTimestamp = now,
            PartialFrameStartTimestamp = 0,
            PartialBytesAccumulated = 0,
            HasPartialFrame = false,
        };
    }

    /// <summary>
    /// Records that a connection has buffered an incomplete frame (Slowloris tracking).
    /// Do not reset <c>PartialFrameStartTimestamp</c> on subsequent 1-byte drips so slow-drip attacks time out deterministically.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordPartialFrameDrip(ConnectionId connectionId, int bytesReceived)
    {
        long now = _timeProvider.GetTimestamp();
        if (!_entries.TryGetValue(connectionId, out ConnectionEntry entry))
        {
            entry = new ConnectionEntry
            {
                LastActivityTimestamp = now,
                PartialFrameStartTimestamp = now,
                PartialBytesAccumulated = bytesReceived,
                HasPartialFrame = true,
            };
        }
        else
        {
            entry.LastActivityTimestamp = now;
            if (!entry.HasPartialFrame)
            {
                entry.PartialFrameStartTimestamp = now;
                entry.HasPartialFrame = true;
            }

            entry.PartialBytesAccumulated += bytesReceived;
        }

        _entries[connectionId] = entry;
    }

    /// <summary>Removes a connection from the timing wheel upon disconnect.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(ConnectionId connectionId) => _entries.Remove(connectionId);

    /// <summary>
    /// Sweeps all tracked connections and writes expired <see cref="ConnectionId"/> values into <paramref name="expiredBuffer"/>.
    /// Expired connections are automatically removed from the wheel.
    /// </summary>
    public int SweepExpired(Span<ConnectionId> expiredBuffer)
    {
        if (_entries.Count == 0 || expiredBuffer.IsEmpty)
        {
            return 0;
        }

        long now = _timeProvider.GetTimestamp();
        if (_lastSweepTimestamp != 0 && _timeProvider.GetElapsedTime(_lastSweepTimestamp, now) < TimeSpan.FromMilliseconds(50))
        {
            return 0;
        }

        _lastSweepTimestamp = now;
        int count = CollectExpiredIds(now, expiredBuffer);
        for (int i = 0; i < count; i++)
        {
            _entries.Remove(expiredBuffer[i]);
        }

        return count;
    }

    private int CollectExpiredIds(long now, Span<ConnectionId> expiredBuffer)
    {
        int count = 0;
        foreach (KeyValuePair<ConnectionId, ConnectionEntry> kvp in _entries)
        {
            ConnectionEntry entry = kvp.Value;
            if (IsEntryExpired(in entry, now))
            {
                expiredBuffer[count++] = kvp.Key;
                if (count >= expiredBuffer.Length)
                {
                    break;
                }
            }
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEntryExpired(in ConnectionEntry entry, long now)
    {
        if (entry.HasPartialFrame)
        {
            TimeSpan partialElapsed = _timeProvider.GetElapsedTime(entry.PartialFrameStartTimestamp, now);
            if (partialElapsed >= _frameReadTimeout)
            {
                return true;
            }
        }

        TimeSpan idleElapsed = _timeProvider.GetElapsedTime(entry.LastActivityTimestamp, now);
        return idleElapsed >= _idleTimeout;
    }
}
