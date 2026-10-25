using System;
using Hayabusa.Core.Buffers;
using Hayabusa.Core.Transport;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Hayabusa.Transport;

/// <summary>
/// Factory that creates an <see cref="ITransportBackend"/> according to <c>HAYABUSA_TRANSPORT</c> (<c>"auto"</c>, <c>"io_uring"</c>, <c>"socket"</c>, or <c>"inmemory"</c>)
/// with automatic graceful fallback to <see cref="SocketEventTransport"/> when <c>io_uring</c> is unavailable.
/// </summary>
public static class TransportFactory
{
    /// <summary>
    /// Creates a transport backend instance for a worker thread.
    /// </summary>
    /// <param name="transportMode">Configured transport mode (<c>"auto"</c>, <c>"io_uring"</c>, <c>"socket"</c>, or <c>"inmemory"</c>).</param>
    /// <param name="slabPool">Optional pre-allocated pinned slab pool.</param>
    /// <param name="logger">Optional structured logger for reporting transport selection or fallback.</param>
    public static ITransportBackend Create(
        string? transportMode = "auto",
        PinnedSlabPool? slabPool = null,
        ILogger? logger = null)
    {
        string normalized = string.IsNullOrWhiteSpace(transportMode)
            ? "auto"
            : transportMode.Trim().ToUpperInvariant();

        return normalized switch
        {
            "INMEMORY" => new InMemoryTransport(slabPool),
            "SOCKET" => CreateSocketTransport(slabPool, logger, "Explicitly configured socket transport."),
            "IO_URING" => CreateIoUringOrFallback(slabPool, logger),
            _ => CreateIoUringOrFallback(slabPool, logger),
        };
    }

    private static ITransportBackend CreateIoUringOrFallback(PinnedSlabPool? slabPool, ILogger? logger)
    {
        IoUringTransport? uring = null;
        try
        {
            if (IoUringTransport.TryCreate(slabPool, 256u, out uring, out string message))
            {
                logger?.ZLogInformation($"Selected transport backend: io_uring ({message})");
                IoUringTransport result = uring;
                uring = null;
                return result;
            }

            return CreateSocketTransport(slabPool, logger, message);
        }
        finally
        {
            uring?.Dispose();
        }
    }

    private static SocketEventTransport CreateSocketTransport(
        PinnedSlabPool? slabPool,
        ILogger? logger,
        string reason)
    {
        logger?.ZLogInformation($"Selected transport backend: socket ({reason})");
        return new SocketEventTransport(slabPool);
    }
}
