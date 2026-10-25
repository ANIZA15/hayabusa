using System;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Hayabusa.Runtime.Hosting;

/// <summary>
/// Server configuration options populated automatically from 12-Factor environment variables
/// (<c>HAYABUSA_*</c>) and command-line arguments (<c>--bind</c>, <c>--http-port</c>, <c>--rpc-port</c>, <c>--workers</c>, <c>--tick-rate</c>, <c>--transport</c>).
/// </summary>
public sealed class HayabusaServerOptions
{
    /// <summary>Gets or sets the IP address to bind (default: <c>"0.0.0.0"</c>).</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Gets or sets the HTTP / Minimal API listening port (default: <c>5000</c>).</summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>Gets or sets the binary MemoryPack RPC / Room listening port (default: <c>5001</c>).</summary>
    public int RpcPort { get; set; } = 5001;

    /// <summary>Gets or sets the number of dedicated Thread-per-Core workers (<c>0</c> = auto-detect logical CPU cores).</summary>
    public int WorkerThreads { get; set; }

    /// <summary>Gets or sets the RoomActor deterministic tick rate in Hz (default: <c>60</c>).</summary>
    public int TickRate { get; set; } = 60;

    /// <summary>Gets or sets the I/O transport backend mode (<c>"auto"</c>, <c>"io_uring"</c>, <c>"socket"</c>, or <c>"inmemory"</c>).</summary>
    public string Transport { get; set; } = "auto";

    /// <summary>Gets or sets an optional ZLogger-compatible <see cref="ILogger"/> for structured logging.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Gets the resolved positive worker count (using <see cref="Environment.ProcessorCount"/> when <see cref="WorkerThreads"/> &lt;= 0).</summary>
    public int ResolvedWorkerCount => WorkerThreads > 0 ? WorkerThreads : Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Applies environment variables (<c>HAYABUSA_*</c>) and command-line overrides to this options instance.
    /// </summary>
    public void ApplyEnvironmentAndArgs(string[]? args)
    {
        ApplyEnvironmentVariables();
        if (args is not null && args.Length > 0)
        {
            ApplyCommandLineArgs(args);
        }
    }

    private void ApplyEnvironmentVariables()
    {
        BindAddress = ReadStringEnv("HAYABUSA_BIND_ADDRESS", BindAddress);
        HttpPort = ReadIntEnv("HAYABUSA_HTTP_PORT", HttpPort);
        RpcPort = ReadIntEnv("HAYABUSA_RPC_PORT", RpcPort);
        WorkerThreads = ReadIntEnv("HAYABUSA_WORKER_THREADS", WorkerThreads);
        TickRate = ReadIntEnv("HAYABUSA_TICK_RATE", TickRate);
        Transport = ReadStringEnv("HAYABUSA_TRANSPORT", Transport);
    }

    private void ApplyCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (ApplySingleArgPair(args[i], args[i + 1]))
            {
                i++;
            }
        }
    }

    private bool ApplySingleArgPair(string key, string value)
    {
        switch (key)
        {
            case "--bind":
                BindAddress = value;
                return true;
            case "--http-port":
                HttpPort = ParseIntOrDefault(value, HttpPort);
                return true;
            case "--rpc-port":
                RpcPort = ParseIntOrDefault(value, RpcPort);
                return true;
            case "--workers":
                WorkerThreads = ParseIntOrDefault(value, WorkerThreads);
                return true;
            case "--tick-rate":
                TickRate = ParseIntOrDefault(value, TickRate);
                return true;
            case "--transport":
                Transport = value;
                return true;
            default:
                return false;
        }
    }

    private static string ReadStringEnv(string name, string fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return ParseIntOrDefault(raw, fallback);
    }

    private static int ParseIntOrDefault(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
}
