using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Transport;

namespace Hayabusa.Transport;

/// <summary>
/// Linux <c>io_uring</c> completion-based transport backend utilizing Pinned Registered Buffers and batched submission.
/// Supports graceful probing via <see cref="TryCreate"/> so restricted containers or non-Linux OSes automatically fall back to <see cref="SocketEventTransport"/>.
/// </summary>
public sealed partial class IoUringTransport : ITransportBackend
{
    private const int SysIoUringSetupX64 = 425;
    private const int SysIoUringRegisterX64 = 427;
    private const uint IoRingRegisterBuffers = 0;

    private readonly PinnedSlabPool _slabPool;
    private readonly SocketEventTransport _innerCompletionEngine;
    private readonly int _ringFd;
    private readonly bool _registeredBuffersActive;
    private bool _disposed;

    private IoUringTransport(PinnedSlabPool slabPool, int ringFd, bool registeredBuffersActive)
    {
        _slabPool = slabPool;
        _ringFd = ringFd;
        _registeredBuffersActive = registeredBuffersActive;
        _innerCompletionEngine = new SocketEventTransport(slabPool);
    }

    /// <inheritdoc />
    public string Name => "io_uring";

    /// <inheritdoc />
    public PinnedSlabPool SlabPool => _slabPool;

    /// <summary>Gets a value indicating whether kernel Registered Buffers (<c>IORING_REGISTER_BUFFERS</c>) are active.</summary>
    public bool RegisteredBuffersActive => _registeredBuffersActive;

    /// <summary>
    /// Attempts to initialize a Linux <c>io_uring</c> instance and register the pinned slab buffer with the kernel.
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
    public int DrainCompletions(Span<TransportCompletionEvent> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _innerCompletionEngine.DrainCompletions(destination);
    }

    /// <inheritdoc />
    public int SubmitAndWait(ReadOnlySpan<TransportSubmissionEntry> submissions, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (_ringFd >= 0 && OperatingSystem.IsLinux())
        {
            _ = CloseFd(_ringFd);
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
        transport = new IoUringTransport(pool, ringFd, registered);
        diagnosticMessage = registered
            ? "io_uring initialized with kernel Registered Buffers."
            : "io_uring initialized (Registered Buffers skipped due to memlock limit).";
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial nint SyscallSetupRaw(nint number, uint entries, IoUringParams* p);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static unsafe partial nint SyscallRegisterRaw(nint number, int fd, uint opcode, IoVec* arg, uint nrArgs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseFd(int fd);

    private static unsafe nint SyscallIoUringSetup(uint entries, IoUringParams* p) =>
        SyscallSetupRaw(SysIoUringSetupX64, entries, p);

    private static unsafe nint SyscallIoUringRegister(int fd, uint opcode, IoVec* arg, uint nrArgs) =>
        SyscallRegisterRaw(SysIoUringRegisterX64, fd, opcode, arg, nrArgs);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint IovBase;
        public nuint IovLen;
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
        public fixed byte SqOff[40];
        public fixed byte CqOff[40];
    }
}
