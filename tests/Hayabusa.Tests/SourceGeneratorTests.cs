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
    private static readonly CSharpParseOptions s_parseOptions = new(LanguageVersion.Preview);
    private static readonly MetadataReference[] s_metadataReferences = CreateMetadataReferences();
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

    [Fact]
    public void Generator_WhenServiceClassIsAbstract_ReportsHAYABUSA001()
    {
        const string source = """
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public abstract partial class AbstractService
            {
                [Rpc(1)]
                public int Ping(int x) => x;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA001");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Generator_WhenMethodIdIsZeroOrNegative_ReportsHAYABUSA002(int invalidId)
    {
        string source = $$"""
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class NegativeIdService
            {
                [Rpc({{invalidId}})]
                public int Call(int x) => x;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA002");
    }

    [Fact]
    public void Generator_WhenReturnTypeOrTupleElementIsNotMemoryPackable_ReportsHAYABUSA003()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            public sealed class BadDto { }

            [HayabusaService]
            public sealed partial class UnpackableReturnService
            {
                [Rpc(1)]
                public ValueTask<BadDto> GetBadAsync() => default;

                [Rpc(2)]
                public BadDto GetBadSync() => new();

                [Rpc(3)]
                public int CallBadTuple((int A, BadDto B) input) => input.A;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Equal(3, CountDiagnosticById(diagnostics, "HAYABUSA003"));
    }

    [Fact]
    public void Generator_WhenRpcMethodIsAsyncVoidOrNonGenericTask_ReportsHAYABUSA004()
    {
        const string source = """
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class BadAsyncVoidService
            {
                [Rpc(1)]
                public async void FireAndCrash() => await Task.CompletedTask;

                [Rpc(2)]
                public Task PlainTaskAsync() => Task.CompletedTask;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Equal(2, CountDiagnosticById(diagnostics, "HAYABUSA004"));
    }

    [Theory]
    [InlineData("no-leading-slash")]
    [InlineData("")]
    [InlineData("/users/{unclosed")]
    [InlineData("/users/{}")]
    [InlineData("/users/}bad{")]
    public void Generator_WhenHttpRouteTemplateHasInvalidSyntax_ReportsHAYABUSA005(string badTemplate)
    {
        string source = $$"""
            using Hayabusa.Rpc;

            namespace TestApp;

            [HayabusaService]
            public sealed partial class MalformedRouteSyntaxService
            {
                [Post("{{badTemplate}}")]
                public void Handle(int id) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out _);

        Assert.Contains(diagnostics, static d => d.Id == "HAYABUSA005");
    }

    [Fact]
    public void Generator_GlobalNamespaceAndAllRouteParameterTypes_GeneratesValidCompilableCode()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Hayabusa.Rpc;

            [HayabusaService]
            public sealed partial class GlobalRouteService
            {
                [Rpc(1)]
                public ValueTask PingUnitAsync() => ValueTask.CompletedTask;

                [Get("/by-int/{id}")]
                public void ByInt(int id) { }

                [Get("/by-long/{id}")]
                public void ByLong(long id) { }

                [Get("/by-ulong/{id}")]
                public void ByULong(ulong id) { }

                [Get("/by-uint/{id}")]
                public void ByUInt(uint id) { }

                [Get("/by-guid/{id}")]
                public void ByGuid(Guid id) { }

                [Get("/by-span/{slug}")]
                public void BySpan(ReadOnlySpan<byte> slug) { }

                [Post("/by-string/{name}")]
                public void ByString(string name) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunGeneratorAndGetDiagnostics(source, out Compilation outputCompilation);

        Assert.Empty(diagnostics);
        Assert.Empty( FilterErrors(outputCompilation.GetDiagnostics()));
    }

    [Fact]
    public void EquatableArrayAndSourceSpan_EqualityOperatorsAndIndexing_BehaveDeterministically()
    {
        EquatableArray<int> defaultArr = default;
        EquatableArray<int> emptyArr = EquatableArray<int>.Empty;
        var arr1 = new EquatableArray<int>([10, 20]);
        var arr2 = new EquatableArray<int>([10, 20]);
        var arr3 = new EquatableArray<int>([10, 99]);
        var arrShort = new EquatableArray<int>([10]);

        Assert.Equal(0, defaultArr.Length);
        Assert.True(defaultArr == emptyArr);
        Assert.True(arr1 == arr2);
        Assert.True(arr1 != arr3);
        Assert.True(arr1 != arrShort);
        Assert.Equal(20, arr1[1]);
        Assert.True(arr1.Equals((object)arr2));
        Assert.False(arr1.Equals((object)"not-an-array"));

        EquatableSourceSpan noneSpan = EquatableSourceSpan.FromLocation(Location.None);
        Assert.Equal(Location.None, noneSpan.ToRoslynLocation());

        var validSpan = new EquatableSourceSpan("File.cs", (0, 5), (0, 0, 0, 5));
        Location reconstructed = validSpan.ToRoslynLocation();
        Assert.Equal("File.cs", reconstructed.GetLineSpan().Path);
    }

    [Fact]
    public async Task GeneratedParameterizedHttpAndUnitRpcService_ExecutesAllGeneratedPathsAtRuntime()
    {
        using var writer = new Hayabusa.Core.Buffers.PooledBufferWriter(1024);
        var ctx = new HayabusaContext(CoreIndex.Zero, writer);
        var svc = new ComprehensiveGeneratedTestService();
        var dispatcher = new Hayabusa.Rpc.Dispatcher.RpcDispatcher();
        dispatcher.RegisterService(svc);

        using var client = ComprehensiveGeneratedTestServiceClient.CreateInMemory(dispatcher);
        await client.TriggerVoidWithArgAsync(42);
        await client.TriggerUnitAsync();
        Assert.Equal(42, svc.LastRecordedInt);
        Assert.True(svc.UnitTriggered);

        // Exercise generated HTTP routes (int, string, Post, and invalid int segment)
        _ = Hayabusa.Rpc.Http.HttpParser.TryParseRequest("GET /items/123 HTTP/1.1\r\n\r\n"u8, out var intReq);
        Assert.True(svc.TryDispatchHttp(ctx, in intReq, writer));
        Assert.Equal(123, svc.LastRecordedInt);

        _ = Hayabusa.Rpc.Http.HttpParser.TryParseRequest("GET /items/not-a-number HTTP/1.1\r\n\r\n"u8, out var badIntReq);
        Assert.False(svc.TryDispatchHttp(ctx, in badIntReq, writer));

        _ = Hayabusa.Rpc.Http.HttpParser.TryParseRequest("POST /tags/hayabusa HTTP/1.1\r\n\r\n"u8, out var strReq);
        Assert.True(svc.TryDispatchHttp(ctx, in strReq, writer));
        Assert.Equal("hayabusa", svc.LastRecordedTag);
    }

    private static int CountDiagnosticById(ImmutableArray<Diagnostic> diagnostics, string id)
    {
        int count = 0;
        for (int i = 0; i < diagnostics.Length; i++)
        {
            if (diagnostics[i].Id == id)
            {
                count++;
            }
        }

        return count;
    }

    private static ImmutableArray<Diagnostic> FilterErrors(ImmutableArray<Diagnostic> diagnostics)
    {
        ImmutableArray<Diagnostic>.Builder builder = ImmutableArray.CreateBuilder<Diagnostic>();
        for (int i = 0; i < diagnostics.Length; i++)
        {
            if (diagnostics[i].Severity == DiagnosticSeverity.Error)
            {
                builder.Add(diagnostics[i]);
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> RunGeneratorAndGetDiagnostics(
        string source,
        out Compilation outputCompilation)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, options: s_parseOptions, path: "TestService.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: [syntaxTree],
            references: s_metadataReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new HayabusaIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            parseOptions: s_parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out ImmutableArray<Diagnostic> diagnostics);
        return diagnostics;
    }

    private static MetadataReference[] CreateMetadataReferences() =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Buffers.IBufferWriter<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Net.Http.HttpMethod).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(ValueTask).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(UniTask).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(ConnectionId).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(HayabusaServiceAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(RoomActor).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(MemoryPackableAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Memory").Location),
        MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location),
    ];
}

[HayabusaService]
public sealed partial class ComprehensiveGeneratedTestService
{
    public int LastRecordedInt { get; private set; }

    public string LastRecordedTag { get; private set; } = string.Empty;

    public bool UnitTriggered { get; private set; }

    [Rpc(MethodId = 1)]
    public void TriggerVoidWithArg(int value)
    {
        LastRecordedInt = value;
    }

    [Rpc(MethodId = 2)]
    public ValueTask TriggerUnitAsync()
    {
        UnitTriggered = true;
        return ValueTask.CompletedTask;
    }

    [Get("/items/{id}")]
    public void GetItem(int id)
    {
        LastRecordedInt = id;
    }

    [Post("/tags/{tag}")]
    public void PostTag(string tag)
    {
        LastRecordedTag = tag;
    }
}
