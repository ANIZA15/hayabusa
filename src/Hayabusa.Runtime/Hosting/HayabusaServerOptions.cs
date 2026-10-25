using System;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Hayabusa.Runtime.Hosting;

/// <summary>
/// Defines the operational role of a Hayabusa server instance.
/// Allows running Hayabusa as a dedicated stateless Web/API server, a dedicated real-time Game/RPC server,
/// or a hybrid unified server handling both workloads on the same Thread-per-Core runtime.
/// </summary>
public enum HayabusaServerRole : byte
{
    /// <summary>
    /// Hybrid mode (default): Accepts both HTTP/Web requests and binary RPC / real-time Game Room ticks.
    /// </summary>
    WebAndGame = 0,

    /// <summary>
    /// Dedicated stateless Web/HTTP server mode: Enables HTTP/1.1 endpoints while disabling Game Room ticks
    /// and rejecting binary game RPC frames.
    /// </summary>
    WebOnly = 1,

    /// <summary>
    /// Dedicated Game/RPC server mode: Enables binary RPC (MemoryPack / custom frames) and deterministic
    /// LogicLooper-style Game Room ticks while disabling HTTP endpoint parsing.
    /// </summary>
    GameOnly = 2,
}

/// <summary>
/// Server configuration options populated automatically from 12-Factor environment variables
/// (<c>HAYABUSA_*</c>) and command-line arguments (<c>--role</c>, <c>--bind</c>, <c>--http-port</c>, <c>--rpc-port</c>, <c>--workers</c>, <c>--tick-rate</c>, <c>--transport</c>).
/// </summary>
public sealed class HayabusaServerOptions
{
    /// <summary>Gets or sets the operational role of the server (default: <see cref="HayabusaServerRole.WebAndGame"/>).</summary>
    public HayabusaServerRole Role { get; set; } = HayabusaServerRole.WebAndGame;

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

    /// <summary>Gets a value indicating whether HTTP / Web request processing is enabled under the current <see cref="Role"/>.</summary>
    public bool IsWebEnabled => Role is HayabusaServerRole.WebAndGame or HayabusaServerRole.WebOnly;

    /// <summary>Gets a value indicating whether binary RPC and Game Room tick loops are enabled under the current <see cref="Role"/>.</summary>
    public bool IsGameEnabled => Role is HayabusaServerRole.WebAndGame or HayabusaServerRole.GameOnly;

    /// <summary>Gets the resolved positive worker count (using <see cref="Environment.ProcessorCount"/> when <see cref="WorkerThreads"/> &lt;= 0).</summary>
    public int ResolvedWorkerCount => WorkerThreads > 0 ? WorkerThreads : Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Parses a role string (<c>"web"</c>, <c>"game"</c>, <c>"both"</c>, <c>"all"</c>, <c>"hybrid"</c>, <c>"web-only"</c>, <c>"game-only"</c>)
    /// into a <see cref="HayabusaServerRole"/> value.
    /// </summary>
    public static HayabusaServerRole ParseRoleOrDefault(string? raw, HayabusaServerRole fallback = HayabusaServerRole.WebAndGame)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToUpperInvariant() switch
        {
            "WEB" or "WEBONLY" or "WEB-ONLY" or "HTTP" => HayabusaServerRole.WebOnly,
            "GAME" or "GAMEONLY" or "GAME-ONLY" or "RPC" => HayabusaServerRole.GameOnly,
            "BOTH" or "ALL" or "HYBRID" or "WEBANDGAME" or "WEB-AND-GAME" => HayabusaServerRole.WebAndGame,
            _ => fallback,
        };
    }

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
        Role = ParseRoleOrDefault(Environment.GetEnvironmentVariable("HAYABUSA_ROLE"), Role);
        BindAddress = ReadStringEnv("HAYABUSA_BIND_ADDRESS", BindAddress);
        HttpPort = ReadIntEnv("HAYABUSA_HTTP_PORT", HttpPort);
        RpcPort = ReadIntEnv("HAYABUSA_RPC_PORT", RpcPort);
        WorkerThreads = ReadIntEnv("HAYABUSA_WORKER_THREADS", WorkerThreads);
        TickRate = ReadIntEnv("HAYABUSA_TICK_RATE", TickRate);
        Transport = ReadStringEnv("HAYABUSA_TRANSPORT", Transport);
    }

    private void ApplyCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (ApplyStandaloneRoleFlag(args[i]))
            {
                continue;
            }

            if (i < args.Length - 1 && ApplySingleArgPair(args[i], args[i + 1]))
            {
                i++;
            }
        }
    }

    private bool ApplyStandaloneRoleFlag(string arg)
    {
        switch (arg)
        {
            case "--web-only":
                Role = HayabusaServerRole.WebOnly;
                return true;
            case "--game-only":
                Role = HayabusaServerRole.GameOnly;
                return true;
            case "--web-and-game":
                Role = HayabusaServerRole.WebAndGame;
                return true;
            default:
                return false;
        }
    }

    private bool ApplySingleArgPair(string key, string value)
    {
        switch (key)
        {
            case "--role":
                Role = ParseRoleOrDefault(value, Role);
                return true;
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
