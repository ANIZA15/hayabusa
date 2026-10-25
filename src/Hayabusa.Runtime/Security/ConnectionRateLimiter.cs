using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Runtime.Security;

/// <summary>
/// Lock-free per-connection Token Bucket rate limiter and in-flight backpressure guard.
/// Defends against Packet Floods, Rapid Reset attacks, and Room Speed Hacks on a worker core.
/// </summary>
public sealed class ConnectionRateLimiter
{
    private struct BucketState
    {
        public double Tokens;
        public long LastRefillTimestamp;
        public int InFlightCount;
    }

    private readonly TimeProvider _timeProvider;
    private readonly double _maxBurstTokens;
    private readonly double _refillTokensPerSecond;
    private readonly int _maxInFlightRequests;
    private readonly Dictionary<ConnectionId, BucketState> _buckets = new(256);

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionRateLimiter"/> class.
    /// </summary>
    public ConnectionRateLimiter(
        TimeProvider? timeProvider = null,
        int maxBurstTokens = 120,
        double refillTokensPerSecond = 60.0,
        int maxInFlightRequests = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBurstTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refillTokensPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlightRequests);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxBurstTokens = maxBurstTokens;
        _refillTokensPerSecond = refillTokensPerSecond;
        _maxInFlightRequests = maxInFlightRequests;
    }

    /// <summary>Gets the number of tracked connections in the rate limiter.</summary>
    public int TrackedCount => _buckets.Count;

    /// <summary>
    /// Attempts to acquire <paramref name="tokenCost"/> rate-limit tokens and 1 in-flight slot for <paramref name="connectionId"/>.
    /// Returns <c>false</c> when the client is flooding or exceeding backpressure limits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquire(ConnectionId connectionId, double tokenCost = 1.0)
    {
        long now = _timeProvider.GetTimestamp();
        if (!_buckets.TryGetValue(connectionId, out BucketState state))
        {
            state = new BucketState
            {
                Tokens = _maxBurstTokens - tokenCost,
                LastRefillTimestamp = now,
                InFlightCount = 1,
            };
            _buckets[connectionId] = state;
            return true;
        }

        RefillTokens(ref state, now);
        if (state.Tokens < tokenCost || state.InFlightCount >= _maxInFlightRequests)
        {
            _buckets[connectionId] = state;
            return false;
        }

        state.Tokens -= tokenCost;
        state.InFlightCount++;
        _buckets[connectionId] = state;
        return true;
    }

    /// <summary>Releases 1 in-flight slot when request processing completes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseInFlight(ConnectionId connectionId)
    {
        if (_buckets.TryGetValue(connectionId, out BucketState state))
        {
            state.InFlightCount = Math.Max(0, state.InFlightCount - 1);
            _buckets[connectionId] = state;
        }
    }

    /// <summary>Removes all rate-limit state for a disconnected client.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(ConnectionId connectionId) => _buckets.Remove(connectionId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefillTokens(ref BucketState state, long now)
    {
        double elapsedSeconds = _timeProvider.GetElapsedTime(state.LastRefillTimestamp, now).TotalSeconds;
        if (elapsedSeconds > 0.0)
        {
            state.Tokens = Math.Min(_maxBurstTokens, state.Tokens + (elapsedSeconds * _refillTokensPerSecond));
            state.LastRefillTimestamp = now;
        }
    }
}
