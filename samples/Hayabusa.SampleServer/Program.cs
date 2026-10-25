using System;
using System.Threading;
using System.Threading.Tasks;
using Hayabusa.Core.Primitives;
using Hayabusa.Runtime.Hosting;

namespace Hayabusa.SampleServer;

/// <summary>
/// Entry point for the Hayabusa Sample Server demonstrating both Attribute-based Source Generator RPC/Room services
/// and Minimal API style endpoints.
/// </summary>
public static class Program
{
    /// <summary>
    /// Builds and starts the Hayabusa Sample Server according to the configured <see cref="HayabusaServerRole"/>
    /// (<c>WebOnly</c>, <c>GameOnly</c>, or <c>WebAndGame</c> hybrid).
    /// </summary>
    public static HayabusaApp CreateSampleApp(string[]? args = null)
    {
        HayabusaApp app = HayabusaApp.CreateBuilder(args)
            .UseThreadPerCore(static options =>
            {
                if (options.TickRate <= 0)
                {
                    options.TickRate = 60;
                }
            })
            .Build();

        if (app.Options.IsGameEnabled)
        {
            _ = app.RegisterRoom<BattleRoomService>(new RoomId(1UL));

            app.MapRpc<MoveCommand, WorldSnapshot>(
                methodId: 2,
                static (ctx, cmd) => new WorldSnapshot(ctx.CurrentTick, cmd.X >= 0 ? 1 : 0));
        }

        if (app.Options.IsWebEnabled)
        {
            app.MapGet("/health", static ctx => ctx.WriteUtf8("OK"u8));
        }

        return app;
    }

    /// <summary>Application entry point.</summary>
    public static async Task Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using HayabusaApp app = CreateSampleApp(args);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (HasSmokeTestFlag(args))
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        }

        await app.RunAsync(cts.Token);
    }

    private static bool HasSmokeTestFlag(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--smoke-test", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
