using Microsoft.CodeAnalysis;

namespace Hayabusa.Generator;

/// <summary>
/// Defines Roslyn <see cref="DiagnosticDescriptor"/> rules (<c>HAYABUSA001</c> through <c>HAYABUSA005</c>)
/// emitted at compile time for invalid Hayabusa service definitions.
/// </summary>
public static class HayabusaDiagnostics
{
    private const string Category = "Hayabusa.Generator";

    /// <summary>HAYABUSA001: Service type with [HayabusaService] must be declared partial and non-abstract.</summary>
    public static readonly DiagnosticDescriptor ServiceMustBePartial = new(
        id: "HAYABUSA001",
        title: "Hayabusa service class must be partial",
        messageFormat: "Service class '{0}' marked with [HayabusaService] must be declared 'partial' and not abstract",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>HAYABUSA002: Duplicate or non-positive MethodId in [Rpc].</summary>
    public static readonly DiagnosticDescriptor DuplicateOrInvalidMethodId = new(
        id: "HAYABUSA002",
        title: "Duplicate or invalid RPC MethodId",
        messageFormat: "RPC method '{0}' specifies duplicate or invalid MethodId '{1}'. Each [Rpc] within a service must have a unique positive MethodId.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>HAYABUSA003: RPC parameter or return payload type is not MemoryPackable or primitive.</summary>
    public static readonly DiagnosticDescriptor TypeNotMemoryPackable = new(
        id: "HAYABUSA003",
        title: "RPC payload type must be MemoryPackable",
        messageFormat: "Type '{0}' used in RPC method '{1}' must be a built-in primitive or marked with [MemoryPackable]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>HAYABUSA004: Async RPC method must return ValueTask, ValueTask&lt;T&gt;, UniTask, or UniTask&lt;T&gt; (Task and void are prohibited).</summary>
    public static readonly DiagnosticDescriptor InvalidRpcReturnType = new(
        id: "HAYABUSA004",
        title: "RPC method must return ValueTask or UniTask",
        messageFormat: "RPC method '{0}' returns '{1}', which is prohibited on Hayabusa hot paths. Use ValueTask, ValueTask<T>, UniTask, or UniTask<T>.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>HAYABUSA005: HTTP route template syntax or bound parameter name mismatch.</summary>
    public static readonly DiagnosticDescriptor InvalidHttpRouteTemplate = new(
        id: "HAYABUSA005",
        title: "Invalid HTTP route template or unbound route parameter",
        messageFormat: "HTTP endpoint '{0}' has an invalid route template '{1}': {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
