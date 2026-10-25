using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Hayabusa.Core.Primitives;

namespace Hayabusa.Runtime.Threading;

/// <summary>
/// Represents an immutable cross-core RPC command forwarded from a socket acceptor/reader core
/// to the target <see cref="RoomId"/>'s affinity-pinned <see cref="CoreIndex"/>.
/// </summary>
/// <param name="SourceCore">The worker core that received the network frame.</param>
/// <param name="ConnectionId">The client connection identifier.</param>
/// <param name="RoomId">The target room identifier.</param>
/// <param name="PayloadFrame">Raw RPC frame bytes (Header + Payload).</param>
public readonly record struct CrossCoreRpcMessage(
    CoreIndex SourceCore,
    ConnectionId ConnectionId,
    RoomId RoomId,
    ReadOnlyMemory<byte> PayloadFrame);

/// <summary>
/// Bounded lock-free Multi-Producer Single-Consumer (MPSC) ring buffer mailbox based on Vyukov's sequence ring.
/// Allows any worker core to enqueue messages to a target core without locks or heap allocations.
/// </summary>
/// <typeparam name="TMessage">The value-type message stored in the ring.</typeparam>
public sealed class MpscMailbox<TMessage>
    where TMessage : struct
{
    private struct Slot
    {
        public int Sequence;
        public TMessage Item;
    }

    private readonly Slot[] _buffer;
    private readonly int _mask;
    private int _enqueuePosition;
    private int _dequeuePosition;

    /// <summary>
    /// Initializes a new instance of the <see cref="MpscMailbox{TMessage}"/> class.
    /// </summary>
    /// <param name="capacityPowerOfTwo">Ring capacity (rounded up to a power of two, minimum 16).</param>
    public MpscMailbox(int capacityPowerOfTwo = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityPowerOfTwo);
        int size = NormalizePowerOfTwo(Math.Max(16, capacityPowerOfTwo));
        _mask = size - 1;
        _buffer = new Slot[size];
        for (int i = 0; i < size; i++)
        {
            _buffer[i].Sequence = i;
        }
    }

    /// <summary>Gets the ring buffer capacity.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Thread-safe lock-free enqueue callable from any producer core.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in TMessage item)
    {
        SpinWait spinner = default;
        while (true)
        {
            int pos = Volatile.Read(ref _enqueuePosition);
            ref Slot slot = ref _buffer[pos & _mask];
            int seq = Volatile.Read(ref slot.Sequence);
            int diff = seq - pos;

            if (diff == 0)
            {
                if (Interlocked.CompareExchange(ref _enqueuePosition, pos + 1, pos) == pos)
                {
                    slot.Item = item;
                    Volatile.Write(ref slot.Sequence, pos + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                return false;
            }
            else
            {
                spinner.SpinOnce();
            }
        }
    }

    /// <summary>
    /// Single-consumer dequeue called exclusively by the owning worker thread during Phase 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out TMessage item)
    {
        int pos = _dequeuePosition;
        ref Slot slot = ref _buffer[pos & _mask];
        int seq = Volatile.Read(ref slot.Sequence);
        int diff = seq - (pos + 1);

        if (diff != 0)
        {
            item = default;
            return false;
        }

        _dequeuePosition = pos + 1;
        item = slot.Item;
        slot.Item = default;
        Volatile.Write(ref slot.Sequence, pos + _mask + 1);
        return true;
    }

    private static int NormalizePowerOfTwo(int value)
    {
        int power = 1;
        while (power < value)
        {
            power <<= 1;
        }

        return power;
    }
}
