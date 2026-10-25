using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Hayabusa.Generator.Models;

/// <summary>
/// Value-equatable source location representation that never holds a <see cref="SyntaxNode"/> or <see cref="ISymbol"/>.
/// </summary>
public readonly record struct EquatableSourceSpan(
    string FilePath,
    (int Start, int Length) TextBounds,
    (int StartLine, int StartCharacter, int EndLine, int EndCharacter) LineBounds)
{
    /// <summary>Creates an <see cref="EquatableSourceSpan"/> from a Roslyn <see cref="Location"/>.</summary>
    public static EquatableSourceSpan FromLocation(Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return new EquatableSourceSpan(string.Empty, (0, 0), (0, 0, 0, 0));
        }

        FileLinePositionSpan lineSpan = location.GetLineSpan();
        return new EquatableSourceSpan(
            lineSpan.Path ?? string.Empty,
            (location.SourceSpan.Start, location.SourceSpan.Length),
            (lineSpan.StartLinePosition.Line,
             lineSpan.StartLinePosition.Character,
             lineSpan.EndLinePosition.Line,
             lineSpan.EndLinePosition.Character));
    }

    /// <summary>Reconstructs a Roslyn <see cref="Location"/> for reporting diagnostics.</summary>
    public Location ToRoslynLocation()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            return Location.None;
        }

        var textSpan = new TextSpan(TextBounds.Start, TextBounds.Length);
        var startPos = new LinePosition(LineBounds.StartLine, LineBounds.StartCharacter);
        var endPos = new LinePosition(LineBounds.EndLine, LineBounds.EndCharacter);
        return Location.Create(FilePath, textSpan, new LinePositionSpan(startPos, endPos));
    }
}

/// <summary>
/// Value-equatable diagnostic record emitted during model extraction.
/// </summary>
public readonly record struct EquatableDiagnostic(
    string RuleId,
    EquatableSourceSpan Span,
    (string Arg0, string Arg1, string Arg2) MessageArgs);

/// <summary>
/// Value-equatable representation of a single parameter in an `[Rpc]` method.
/// </summary>
public readonly record struct RpcParameterModel(
    string Name,
    string TypeFullName);

/// <summary>
/// Value-equatable representation of a single `[Rpc]` method inside a `[HayabusaService]`.
/// </summary>
public readonly record struct RpcMethodModel(
    string MethodName,
    int MethodId,
    EquatableArray<RpcParameterModel> Parameters,
    (string FullName, bool HasPayload, bool IsAsync) ResponseType);

/// <summary>
/// Value-equatable representation of a single `[Get]` or `[Post]` HTTP route inside a `[HayabusaService]`.
/// </summary>
public readonly record struct HttpRouteModel(
    string MethodName,
    string HttpVerb,
    string RouteTemplate,
    (string Name, string TypeFullName) BoundParameter);

/// <summary>
/// Root value-equatable model for a `[HayabusaService]` class.
/// Contains zero references to <see cref="ISymbol"/> or <see cref="SyntaxNode"/>.
/// </summary>
public readonly record struct ServiceModel(
    (string Namespace, string ClassName, bool IsRoomActorSubclass, bool IsPartial) Header,
    EquatableArray<RpcMethodModel> RpcMethods,
    EquatableArray<HttpRouteModel> HttpRoutes,
    EquatableArray<EquatableDiagnostic> Diagnostics);

/// <summary>
/// Root value-equatable model for a `[HayabusaHubReceiver]` interface (MagicOnion StreamingHub receiver style).
/// Contains zero references to <see cref="ISymbol"/> or <see cref="SyntaxNode"/>.
/// </summary>
public readonly record struct HubReceiverModel(
    (string Namespace, string InterfaceName, string FullTypeName) Header,
    EquatableArray<RpcMethodModel> Methods,
    EquatableArray<EquatableDiagnostic> Diagnostics);
