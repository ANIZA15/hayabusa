using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Hayabusa.Core.Primitives;
using Hayabusa.Generator;
using Hayabusa.Generator.Models;
using Hayabusa.Rpc;
using Hayabusa.Runtime.Rooms;
using MemoryPack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Hayabusa.Tests;

public sealed class SourceGeneratorTests
{
    [Fact]
    public void Generator_ValidHayabusaService_GeneratesCleanCompilationWithZeroDiagnostics()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;
            using MemoryPack;

            namespace TestApp;

            [MemoryPackable]
            public readonly partial record struct EchoDto(int Value);

            [HayabusaService]
            public sealed partial class EchoService
            {
                [Rpc(MethodId = 1)]
                public ValueTask<EchoDto> EchoAsync(EchoDto input) => new(input);

                [Get("/ping")]
                public void Ping() { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out Compilation outputCompilation);

        Assert.Empty(diagnostics);
        Assert.Contains(outputCompilation.SyntaxTrees, static t => t.FilePath.EndsWith("EchoService.HayabusaService.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_WhenServiceClassIsNotPartial_ReportsHAYABUSA001()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed class NonPartialService
            {
                [Rpc(MethodId = 1)]
                public ValueTask<int> PingAsync(int x) => new(x);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA001");
    }

    [Fact]
    public void Generator_WhenDuplicateMethodIdIsDefined_ReportsHAYABUSA002()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class DuplicateIdService
            {
                [Rpc(MethodId = 10)]
                public ValueTask<int> FirstAsync(int x) => new(x);

                [Rpc(MethodId = 10)]
                public ValueTask<int> SecondAsync(int x) => new(x);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA002");
    }

    [Fact]
    public void Generator_WhenParameterIsNotMemoryPackable_ReportsHAYABUSA003()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            public sealed class PlainUnpackableDto
            {
                public int Id { get; set; }
            }

            [HayabusaService]
            public sealed partial class UnpackableService
            {
                [Rpc(MethodId = 1)]
                public ValueTask<int> CallAsync(PlainUnpackableDto dto) => new(1);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA003");
    }

    [Fact]
    public void Generator_WhenRpcMethodReturnsTaskInsteadOfValueTask_ReportsHAYABUSA004()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class BadTaskService
            {
                [Rpc(MethodId = 1)]
                public Task<int> SlowTaskAsync(int x) => Task.FromResult(x);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA004");
    }

    [Fact]
    public void Generator_WhenHttpRouteTemplateIsMalformedOrUnbound_ReportsHAYABUSA005()
    {
        const string source = """
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class BadRouteService
            {
                [Get("/users/{missingParam}")]
                public void GetUser(string id) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA005");
    }

    [Fact]
    public void EquatableArray_IdenticalContents_ReturnsEqualAndMatchingHashCode()
    {
        var a = new EquatableArray<int>([1, 2, 3]);
        var b = new EquatableArray<int>([1, 2, 3]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Generator_WhenServiceUsesUniTaskAndArrayOrTuplePayloads_GeneratesCleanCompilation()
    {
        const string source = """
            using Cysharp.Threading.Tasks;
            using Hayabusa.Rpc;
            using MemoryPack;

            namespace TestApp;

            [MemoryPackable]
            public readonly partial record struct ItemDto(int Id);

            [HayabusaService]
            public sealed partial class UniTaskInventoryService
            {
                [Rpc(MethodId = 1)]
                public UniTask<ItemDto[]> GetItemsAsync((int Page, int Size) query) =>
                    UniTask.FromResult(new[] { new ItemDto(query.Page) });

                [Rpc(MethodId = 2)]
                public UniTask ClearAsync() => UniTask.CompletedTask;

                [Get("/items/{id}")]
                public void GetItemById(int id) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Generator_MagicOnionStyleIServiceAndHubReceiver_GeneratesBroadcasterBinderAndContractMethods()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;
            using MemoryPack;

            namespace TestApp;

            [MemoryPackable]
            public readonly partial record struct ChatMessage(int UserId, int Code);

            [HayabusaHubReceiver]
            public interface IChatHubReceiver
            {
                void OnMessage(ChatMessage msg);
            }

            public interface IChatService : IService<IChatService>
            {
                ValueTask<ChatMessage> SendAsync(ChatMessage msg);
            }

            [HayabusaService]
            public sealed partial class ChatService : IChatService
            {
                public ValueTask<ChatMessage> SendAsync(ChatMessage msg) => new(msg);
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out Compilation outputCompilation);

        Assert.Empty(diagnostics);
        Assert.Contains(outputCompilation.SyntaxTrees, static t => t.FilePath.EndsWith("IChatHubReceiver.HayabusaHubReceiver.g.cs", StringComparison.Ordinal));
        Assert.Contains(outputCompilation.SyntaxTrees, static t => t.FilePath.EndsWith("ChatService.HayabusaService.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_WhenServiceUsesAutoMethodIdMultiParamsAndSyncReturn_GeneratesCleanCompilation()
    {
        const string source = """
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class CompactInventoryService
            {
                [Rpc]
                public (bool Success, int TotalCount) AddItem(ulong userId, int itemId, int count) =>
                    (true, count + 10);

                [Rpc]
                public void ResetInventory(ulong userId) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out Compilation outputCompilation);

        Assert.Empty(diagnostics);
        Assert.Contains(outputCompilation.SyntaxTrees, static t => t.FilePath.EndsWith("CompactInventoryService.HayabusaService.g.cs", StringComparison.Ordinal));
    }

    private static ImmutableArray<Diagnostic> RunGeneratorAndGetDiagnostics(
        string source,
        out Compilation outputCompilation)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, path: "TestService.cs");
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ValueTask).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(UniTask).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ConnectionId).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(HayabusaServiceAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(RoomActor).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MemoryPackableAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        ];

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new HayabusaIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out ImmutableArray<Diagnostic> diagnostics);
        return diagnostics;
    }
}
