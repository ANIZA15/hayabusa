using System.Text;
using Hayabusa.Generator.Emission;
using Hayabusa.Generator.Models;
using Hayabusa.Generator.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hayabusa.Generator;

/// <summary>
/// Roslyn Incremental Source Generator (<see cref="IIncrementalGenerator"/>) for Hayabusa RPC and HTTP services.
/// Uses <c>ForAttributeWithMetadataName</c> and value-equatable models to guarantee zero IDE cache thrashing.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HayabusaIncrementalGenerator : IIncrementalGenerator
{
    private const string HayabusaServiceAttributeFullName = "Hayabusa.Rpc.HayabusaServiceAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ServiceModel> serviceModels = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: HayabusaServiceAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: ServiceModelParser.ExtractServiceModel);

        context.RegisterSourceOutput(serviceModels, ExecuteEmission);
    }

    private static void ExecuteEmission(SourceProductionContext context, ServiceModel model)
    {
        ReportDiagnostics(context, model.Diagnostics);
        if (!model.Header.IsPartial || model.Diagnostics.Length > 0)
        {
            return;
        }

        string source = ServiceCodeEmitter.EmitServiceSource(in model);
        string hintName = $"{model.Header.ClassName}.HayabusaService.g.cs";
        context.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
    }

    private static void ReportDiagnostics(SourceProductionContext context, EquatableArray<EquatableDiagnostic> diagnostics)
    {
        for (int i = 0; i < diagnostics.Length; i++)
        {
            EquatableDiagnostic diag = diagnostics[i];
            DiagnosticDescriptor descriptor = ResolveDescriptor(diag.RuleId);
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor,
                diag.Span.ToRoslynLocation(),
                diag.MessageArgs.Arg0,
                diag.MessageArgs.Arg1,
                diag.MessageArgs.Arg2));
        }
    }

    private static DiagnosticDescriptor ResolveDescriptor(string ruleId) => ruleId switch
    {
        "HAYABUSA001" => HayabusaDiagnostics.ServiceMustBePartial,
        "HAYABUSA002" => HayabusaDiagnostics.DuplicateOrInvalidMethodId,
        "HAYABUSA003" => HayabusaDiagnostics.TypeNotMemoryPackable,
        "HAYABUSA004" => HayabusaDiagnostics.InvalidRpcReturnType,
        _ => HayabusaDiagnostics.InvalidHttpRouteTemplate,
    };
}
