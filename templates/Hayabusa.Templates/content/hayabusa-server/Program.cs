using System.Threading.Tasks;
using Hayabusa.Core.Primitives;
using Hayabusa.Runtime.Hosting;
using MemoryPack;

namespace HayabusaServerTemplate;

[MemoryPackable]
public readonly partial record struct MoveCommand(float X, float Y, float Z);

[MemoryPackable]
public readonly partial record struct WorldSnapshot(TickNumber Tick, int PlayerCount);

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var app = HayabusaApp.CreateBuilder(args)
            .UseThreadPerCore(static options => options.TickRate = 60)
            .Build();

        app.MapRpc<MoveCommand, WorldSnapshot>(
            methodId: 1,
            static (ctx, _) => new WorldSnapshot(ctx.CurrentTick, 1));

        app.MapGet("/health", static ctx => ctx.WriteUtf8("OK"u8));

        await app.RunAsync();
    }
}
