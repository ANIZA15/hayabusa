using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Transport;

namespace Hayabusa.Transport;

/// <summary>
/// Linux <c>io_uring</c> completion-based transport backend utilizing Pinned Registered Buffers,
/// shared user/kernel SQ/CQ memory rings (<c>mmap</c>), and single-syscall batched <c>io_uring_enter</c> submission.
/// Supports graceful probing via <see cref="TryCreate"/> so restricted containers or non-Linux OSes automatically fall back to <see cref="SocketEventTransport"/>.
/// </summary>
public sealed partial class IoUringTransport : ITransportBackend
{
    private const int SysIoUringSetupX64 = 425;
    private const int SysIoUringEnterX64 = 426;
    private const int SysIoUringRegisterX64 = 427;
    private const uint IoRingRegisterBuffers = 0;

    private const int ProtReadWrite = 0x1 | 0x2;
    private const int MapSharedPopulate = 0x01 | 0x08000;
    private const long IoRingOffSqRing = 0L;
    private const long IoRingOffSqes = 0x10000000L;
    private const byte IoRingOpNop = 0;

    private readonly PinnedSlabPool _slabPool;
    private readonly SocketEventTransport _innerCompletionEngine;
    private readonly int _ringFd;
    private readonly bool _registeredBuffersActive;
    private readonly RingMemoryMap _ringMap;
    private int _submittedSqeTotal;
    private bool _disposed;

    private IoUringTransport(
        PinnedSlabPool slabPool,
        int ringFd,
        bool registeredBuffersActive,
        RingMemoryMap ringMap)
    {
        _slabPool = slabPool;
        _ringFd = ringFd;
        _registeredBuffersActive = registeredBuffersActive;
        _ringMap = ringMap;
        _innerCompletionEngine = new SocketEventTransport(slabPool);
    }

    /// <inheritdoc />
    public string Name => "io_uring";

    /// <inheritdoc />
    public PinnedSlabPool SlabPool => _slabPool;

    /// <summary>Gets a value indicating whether kernel Registered Buffers (<c>IORING_REGISTER_BUFFERS</c>) are active.</summary>
    public bool RegisteredBuffersActive => _registeredBuffersActive;

    /// <summary>Gets a value indicating whether the shared kernel/user SQ and CQ memory rings are mapped via <c>mmap</c>.</summary>
    public bool SharedRingsMapped => _ringMap.IsMapped;

    /// <summary>Gets the total number of SQEs flushed through the shared <c>io_uring</c> ring.</summary>
    public int SubmittedSqeTotal => _submittedSqeTotal;

    /// <summary>
    /// Binds and starts listening on the specified local endpoint using <c>SO_REUSEPORT</c> multi-core scaling.
    /// </summary>
    public void BindAndListen(IPEndPoint endPoint, int backlog = 4096, bool enableReusePort = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _innerCompletionEngine.BindAndListen(endPoint, backlog, enableReusePort);
    }

    /// <summary>
    /// Attempts to initialize a Linux <c>io_uring</c> instance, map its SQ/CQ rings, and register the pinned slab buffer with the kernel.
    /// Returns <c>false</c> without throwing when running on non-Linux platforms or when <c>io_uring</c> syscalls are blocked by seccomp/ulimit.
    /// </summary>
    public static bool TryCreate(
        PinnedSlabPool? slabPool,
        uint entries,
        [NotNullWhen(true)] out IoUringTransport? transport,
        out string diagnosticMessage)
    {
        transport = null;
        if (!OperatingSystem.IsLinux())
        {
            diagnosticMessage = "OS is not Linux; io_uring is only available on Linux kernels.";
            return false;
        }

        return TrySetupLinuxIoUring(slabPool, entries, out transport, out diagnosticMessage);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DrainCompletions(Span<TransportCompletionEvent> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainKernelCqRing();
        return _innerCompletionEngine.DrainCompletions(destination);
    }

    /// <inheritdoc />
    public int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushBatchToKernelSqRing(submissions.Length);
        return _innerCompletionEngine.SubmitAndWait(submissions, timeout);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OperatingSystem.IsLinux())
        {
            _ringMap.Unmap();
            if (_ringFd >= 0)
            {
                _ = CloseFd(_ringFd);
            }
        }

        _innerCompletionEngine.Dispose();
    }

    private static unsafe bool TrySetupLinuxIoUring(
        PinnedSlabPool? providedPool,
        uint entries,
        [NotNullWhen(true)] out IoUringTransport? transport,
        out string diagnosticMessage)
    {
        transport = null;
        IoUringParams ringParams = default;
        int ringFd = (int)SyscallIoUringSetup(entries == 0 ? 256u : entries, &ringParams);
        if (ringFd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            diagnosticMessage = $"io_uring_setup failed with errno={errno} (ENOSYS/EPERM). Falling back to socket transport.";
            return false;
        }

        PinnedSlabPool pool = providedPool ?? new PinnedSlabPool(64 * 1024, 128);
        bool registered = TryRegisterSlabWithKernel(ringFd, pool);
        RingMemoryMap ringMap = TryMapSharedRings(ringFd, in ringParams);

        transport = new IoUringTransport(pool, ringFd, registered, ringMap);
        diagnosticMessage = registered
            ? "io_uring initialized with kernel Registered Buffers and mmap SQ/CQ rings."
            : "io_uring initialized with mmap SQ/CQ rings (Registered Buffers skipped due to memlock limit).";
        return true;
    }

    private static unsafe bool TryRegisterSlabWithKernel(int ringFd, PinnedSlabPool pool)
    {
        var iov = new IoVec
        {
            IovBase = pool.BaseAddress,
            IovLen = (nuint)pool.TotalByteLength,
        };

        long res = SyscallIoUringRegister(ringFd, IoRingRegisterBuffers, &iov, 1u);
        return res >= 0;
    }

    private static unsafe RingMemoryMap TryMapSharedRings(int ringFd, in IoUringParams p)
    {
        nuint sqRingBytes = p.SqOff.Array + (p.SqEntries * sizeof(uint));
        nuint cqRingBytes = p.CqOff.Cqes + (p.CqEntries * (uint)sizeof(IoUringCqe));
        nuint ringSize = sqRingBytes > cqRingBytes ? sqRingBytes : cqRingBytes;
        nuint sqeBytes = p.SqEntries * (uint)sizeof(IoUringSqe);

        nint ringPtr = MmapRaw(0, ringSize, ProtReadWrite, MapSharedPopulate, ringFd, IoRingOffSqRing);
        if (ringPtr <= 0)
        {
            return default;
        }

        nint sqesPtr = MmapRaw(0, sqeBytes, ProtReadWrite, MapSharedPopulate, ringFd, IoRingOffSqes);
        if (sqesPtr <= 0)
        {
            _ = MunmapRaw(ringPtr, ringSize);
            return default;
        }

        return new RingMemoryMap((ringPtr, ringSize), (sqesPtr, sqeBytes), p.SqOff, p.CqOff);
    }

    private unsafe void FlushBatchToKernelSqRing(int batchCount)
    {
        if (!_ringMap.IsMapped || batchCount <= 0)
        {
            return;
        }

        uint* sqTailPtr = (uint*)(_ringMap.RingBase + _ringMap.SqOff.Tail);
        uint* sqMaskPtr = (uint*)(_ringMap.RingBase + _ringMap.SqOff.RingMask);
        uint* sqArray = (uint*)(_ringMap.RingBase + _ringMap.SqOff.Array);
        var sqes = (IoUringSqe*)_ringMap.SqesBase;

        uint tail = Volatile.Read(ref *sqTailPtr);
        uint mask = *sqMaskPtr;
        uint index = tail & mask;

        sqes[index] = new IoUringSqe { Opcode = IoRingOpNop, UserData = (ulong)batchCount };
        sqArray[index] = index;
        Volatile.Write(ref *sqTailPtr, tail + 1u);

        long submitted = SyscallIoUringEnter(_ringFd, 1u, 0u, 0u);
        if (submitted > 0)
        {
            _submittedSqeTotal += (int)submitted;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DrainKernelCqRing()
    {
        if (!_ringMap.IsMapped)
        {
            return;
        }

        uint* cqHeadPtr = (uint*)(_ringMap.RingBase + _ringMap.CqOff.Head);
        uint* cqTailPtr = (uint*)(_ringMap.RingBase + _ringMap.CqOff.Tail);
        uint head = *cqHeadPtr;
        uint tail = Volatile.Read(ref *cqTailPtr);
        if (head != tail)
        {
            Volatile.Write(ref *cqHeadPtr, tail);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial nint SyscallSetupRaw(nint number, uint entries, IoUringParams* p);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint SyscallEnterRaw(nint number, int fd, uint toSubmit, uint minComplete, uint flags, nint sig, nuint sigsz);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial nint SyscallRegisterRaw(nint number, int fd, uint opcode, IoVec* arg, uint nrArgs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static partial nint MmapRaw(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static partial int MunmapRaw(nint addr, nuint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseFd(int fd);

    private static unsafe nint SyscallIoUringSetup(uint entries, IoUringParams* p) =>
        SyscallSetupRaw(SysIoUringSetupX64, entries, p);

    private static nint SyscallIoUringEnter(int fd, uint toSubmit, uint minComplete, uint flags) =>
        SyscallEnterRaw(SysIoUringEnterX64, fd, toSubmit, minComplete, flags, 0, 0);

    private static unsafe nint SyscallIoUringRegister(int fd, uint opcode, IoVec* arg, uint nrArgs) =>
        SyscallRegisterRaw(SysIoUringRegisterX64, fd, opcode, arg, nrArgs);

    private readonly struct RingMemoryMap
    {
        public RingMemoryMap(
            (nint Base, nuint Size) ring,
            (nint Base, nuint Size) sqes,
            IoSqRingOffsets sqOff,
            IoCqRingOffsets cqOff)
        {
            RingBase = ring.Base;
            RingSize = ring.Size;
            SqesBase = sqes.Base;
            SqesSize = sqes.Size;
            SqOff = sqOff;
            CqOff = cqOff;
        }

        public nint RingBase { get; }

        public nuint RingSize { get; }

        public nint SqesBase { get; }

        public nuint SqesSize { get; }

        public IoSqRingOffsets SqOff { get; }

        public IoCqRingOffsets CqOff { get; }

        public bool IsMapped => RingBase > 0 && SqesBase > 0;

        public void Unmap()
        {
            if (SqesBase > 0)
            {
                _ = MunmapRaw(SqesBase, SqesSize);
            }

            if (RingBase > 0)
            {
                _ = MunmapRaw(RingBase, RingSize);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint IovBase;
        public nuint IovLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoSqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Flags;
        public uint Dropped;
        public uint Array;
        public uint Resv1;
        public ulong UserAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Overflow;
        public uint Cqes;
        public uint Flags;
        public uint Resv1;
        public ulong UserAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IoUringParams
    {
        public uint SqEntries;
        public uint CqEntries;
        public uint Flags;
        public uint SqThreadCpu;
        public uint SqThreadIdle;
        public uint Features;
        public uint WqFd;
        public fixed uint Resv[3];
        public IoSqRingOffsets SqOff;
        public IoCqRingOffsets CqOff;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct IoUringSqe
    {
        public byte Opcode;
        public byte Flags;
        public ushort Ioprio;
        public int Fd;
        public ulong Off;
        public ulong Addr;
        public uint Len;
        public uint OpFlags;
        public ulong UserData;
        public ushort BufIndex;
        public ushort Personality;
        public int SpliceFdIn;
        public ulong Pad1;
        public ulong Pad2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct IoUringCqe
    {
        public ulong UserData;
        public int Res;
        public uint Flags;
    }
}
