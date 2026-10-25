using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using Hayabusa.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hayabusa.Generator.Parsing;

/// <summary>
/// Transforms a Roslyn <see cref="GeneratorAttributeSyntaxContext"/> into an immutable, value-equatable <see cref="ServiceModel"/>.
/// Validates rules <c>HAYABUSA001</c> through <c>HAYABUSA005</c>.
/// </summary>
internal static class ServiceModelParser
{
    private const string RpcAttributeFullName = "Hayabusa.Rpc.RpcAttribute";
    private const string GetAttributeFullName = "Hayabusa.Rpc.GetAttribute";
    private const string PostAttributeFullName = "Hayabusa.Rpc.PostAttribute";
    private const string MemoryPackableFullName = "MemoryPack.MemoryPackableAttribute";

    private sealed class BuilderAccumulator
    {
        public HashSet<int> SeenMethodIds { get; } = new();
        public ImmutableArray<RpcMethodModel>.Builder RpcMethods { get; } = ImmutableArray.CreateBuilder<RpcMethodModel>();
        public ImmutableArray<HttpRouteModel>.Builder HttpRoutes { get; } = ImmutableArray.CreateBuilder<HttpRouteModel>();
        public ImmutableArray<EquatableDiagnostic>.Builder Diagnostics { get; } = ImmutableArray.CreateBuilder<EquatableDiagnostic>();
    }

    public static ServiceModel ExtractServiceModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classDecl = (ClassDeclarationSyntax)context.TargetNode;
        var symbol = (INamedTypeSymbol)context.TargetSymbol;

        var acc = new BuilderAccumulator();
        bool isPartial = IsDeclaredPartialAndConcrete(classDecl, symbol);
        if (!isPartial)
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.ServiceMustBePartial.Id,
                EquatableSourceSpan.FromLocation(classDecl.Identifier.GetLocation()),
                (symbol.Name, string.Empty, string.Empty)));
        }

        ParseMembers(symbol, acc);

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        return new ServiceModel(
            (ns, symbol.Name, InheritsRoomActor(symbol), isPartial),
            new EquatableArray<RpcMethodModel>(acc.RpcMethods.ToImmutable()),
            new EquatableArray<HttpRouteModel>(acc.HttpRoutes.ToImmutable()),
            new EquatableArray<EquatableDiagnostic>(acc.Diagnostics.ToImmutable()));
    }

    private static bool IsDeclaredPartialAndConcrete(ClassDeclarationSyntax syntax, INamedTypeSymbol symbol)
    {
        if (symbol.IsAbstract)
        {
            return false;
        }

        foreach (SyntaxToken modifier in syntax.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InheritsRoomActor(INamedTypeSymbol symbol)
    {
        INamedTypeSymbol? baseType = symbol.BaseType;
        while (baseType is not null)
        {
            if (baseType.Name == "RoomActor")
            {
                return true;
            }

            baseType = baseType.BaseType;
        }

        return false;
    }

    private static void ParseMembers(INamedTypeSymbol serviceSymbol, BuilderAccumulator acc)
    {
        ImmutableArray<ISymbol> members = serviceSymbol.GetMembers();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] is IMethodSymbol methodSymbol)
            {
                InspectMethodSymbol(methodSymbol, acc);
            }
        }
    }

    private static void InspectMethodSymbol(IMethodSymbol method, BuilderAccumulator acc)
    {
        ImmutableArray<AttributeData> attrs = method.GetAttributes();
        for (int i = 0; i < attrs.Length; i++)
        {
            DispatchMethodAttribute(method, attrs[i], acc);
        }
    }

    private static void DispatchMethodAttribute(IMethodSymbol method, AttributeData attr, BuilderAccumulator acc)
    {
        string attrName = attr.AttributeClass?.ToDisplayString() ?? string.Empty;
        if (attrName == RpcAttributeFullName)
        {
            TryAddRpcMethod(method, attr, acc);
            return;
        }

        if (attrName == GetAttributeFullName)
        {
            TryAddHttpRoute(method, attr, "Get", acc);
            return;
        }

        if (attrName == PostAttributeFullName)
        {
            TryAddHttpRoute(method, attr, "Post", acc);
        }
    }

    private static void TryAddRpcMethod(IMethodSymbol method, AttributeData rpcAttr, BuilderAccumulator acc)
    {
        int methodId = ExtractMethodId(rpcAttr);
        Location? loc = GetSymbolFirstLocation(method);
        if (methodId <= 0 || !acc.SeenMethodIds.Add(methodId))
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.DuplicateOrInvalidMethodId.Id,
                EquatableSourceSpan.FromLocation(loc),
                (method.Name, methodId.ToString(CultureInfo.InvariantCulture), string.Empty)));
            return;
        }

        if (!TryValidateReturnType(method, acc, out (string FullName, bool HasPayload, bool IsUniTask) resInfo))
        {
            return;
        }

        if (!TryValidateRequestType(method, acc, out (string FullName, bool HasParameter) reqInfo))
        {
            return;
        }

        acc.RpcMethods.Add(new RpcMethodModel(method.Name, methodId, reqInfo, resInfo));
    }

    private static int ExtractMethodId(AttributeData rpcAttr)
    {
        if (rpcAttr.ConstructorArguments.Length > 0 && rpcAttr.ConstructorArguments[0].Value is int ctorId)
        {
            return ctorId;
        }

        ImmutableArray<KeyValuePair<string, TypedConstant>> named = rpcAttr.NamedArguments;
        for (int i = 0; i < named.Length; i++)
        {
            if (named[i].Key == "MethodId" && named[i].Value.Value is int namedId)
            {
                return namedId;
            }
        }

        return 0;
    }

    private static bool TryValidateReturnType(
        IMethodSymbol method,
        BuilderAccumulator acc,
        out (string FullName, bool HasPayload, bool IsUniTask) responseInfo)
    {
        responseInfo = (string.Empty, false, false);
        ITypeSymbol returnType = method.ReturnType;
        string returnName = returnType.Name;

        if (returnName != "ValueTask" && returnName != "UniTask")
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.InvalidRpcReturnType.Id,
                EquatableSourceSpan.FromLocation(GetSymbolFirstLocation(method)),
                (method.Name, returnType.ToDisplayString(), string.Empty)));
            return false;
        }

        bool isUniTask = returnName == "UniTask";
        return TryExtractGenericReturnPayload(method, returnType, isUniTask, acc) is { } valid &&
               AssignOut(valid, out responseInfo);
    }

    private static (string FullName, bool HasPayload, bool IsUniTask)? TryExtractGenericReturnPayload(
        IMethodSymbol method,
        ITypeSymbol returnType,
        bool isUniTask,
        BuilderAccumulator acc)
    {
        if (returnType is not INamedTypeSymbol { IsGenericType: true } genericReturn ||
            genericReturn.TypeArguments.Length != 1)
        {
            return (string.Empty, false, isUniTask);
        }

        ITypeSymbol innerType = genericReturn.TypeArguments[0];
        if (!IsMemoryPackCompatible(innerType))
        {
            ReportNotMemoryPackable(innerType, method, acc);
            return null;
        }

        return (innerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), true, isUniTask);
    }

    private static bool AssignOut<T>(T value, out T target)
    {
        target = value;
        return true;
    }

    private static bool TryValidateRequestType(
        IMethodSymbol method,
        BuilderAccumulator acc,
        out (string FullName, bool HasParameter) requestInfo)
    {
        requestInfo = (string.Empty, false);
        if (method.Parameters.Length == 0)
        {
            return true;
        }

        ITypeSymbol paramType = method.Parameters[0].Type;
        if (!IsMemoryPackCompatible(paramType))
        {
            ReportNotMemoryPackable(paramType, method, acc);
            return false;
        }

        requestInfo = (paramType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), true);
        return true;
    }

    private static void ReportNotMemoryPackable(
        ITypeSymbol offendingType,
        IMethodSymbol method,
        BuilderAccumulator acc)
    {
        acc.Diagnostics.Add(new EquatableDiagnostic(
            HayabusaDiagnostics.TypeNotMemoryPackable.Id,
            EquatableSourceSpan.FromLocation(GetSymbolFirstLocation(method)),
            (offendingType.ToDisplayString(), method.Name, string.Empty)));
    }

    private static bool IsMemoryPackCompatible(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
        {
            return true;
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return IsMemoryPackCompatible(arrayType.ElementType);
        }

        if (type is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            return AreAllTypeArgumentsCompatible(tupleType.TupleElements);
        }

        ImmutableArray<AttributeData> attrs = type.GetAttributes();
        for (int i = 0; i < attrs.Length; i++)
        {
            if (IsMemoryPackAttribute(attrs[i].AttributeClass))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreAllTypeArgumentsCompatible(ImmutableArray<IFieldSymbol> tupleElements)
    {
        for (int i = 0; i < tupleElements.Length; i++)
        {
            if (!IsMemoryPackCompatible(tupleElements[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMemoryPackAttribute(INamedTypeSymbol? attrClass)
    {
        if (attrClass is null)
        {
            return false;
        }

        return attrClass.Name is "MemoryPackableAttribute" or "GenerateTypeScriptAttribute"
               || attrClass.ToDisplayString() == MemoryPackableFullName;
    }

    private static void TryAddHttpRoute(
        IMethodSymbol method,
        AttributeData routeAttr,
        string verb,
        BuilderAccumulator acc)
    {
        string template = routeAttr.ConstructorArguments.Length > 0 && routeAttr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;

        if (!TryValidateRouteTemplate(method, template, out string boundParam, out string errorReason))
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.InvalidHttpRouteTemplate.Id,
                EquatableSourceSpan.FromLocation(GetSymbolFirstLocation(method)),
                (method.Name, template, errorReason)));
            return;
        }

        acc.HttpRoutes.Add(new HttpRouteModel(method.Name, verb, template, boundParam));
    }

    private static bool TryValidateRouteTemplate(
        IMethodSymbol method,
        string template,
        out string boundParam,
        out string errorReason)
    {
        boundParam = string.Empty;
        errorReason = string.Empty;

        if (string.IsNullOrWhiteSpace(template) || template[0] != '/')
        {
            errorReason = "Route template must begin with '/'.";
            return false;
        }

        return TryParseRouteParameterBraces(method, template, out boundParam, out errorReason);
    }

    private static bool TryParseRouteParameterBraces(
        IMethodSymbol method,
        string template,
        out string boundParam,
        out string errorReason)
    {
        boundParam = string.Empty;
        errorReason = string.Empty;

        int openBrace = template.IndexOf('{');
        int closeBrace = template.IndexOf('}');
        if (openBrace < 0 && closeBrace < 0)
        {
            return true;
        }

        if (openBrace < 0 || closeBrace <= openBrace + 1)
        {
            errorReason = "Malformed route parameter braces.";
            return false;
        }

        string paramName = template.Substring(openBrace + 1, closeBrace - openBrace - 1);
        if (!HasParameterNamed(method, paramName))
        {
            errorReason = $"Route parameter '{{{paramName}}}' does not match any method parameter.";
            return false;
        }

        boundParam = paramName;
        return true;
    }

    private static bool HasParameterNamed(IMethodSymbol method, string paramName)
    {
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (string.Equals(method.Parameters[i].Name, paramName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Location? GetSymbolFirstLocation(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : null;
}
