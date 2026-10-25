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
/// Transforms a Roslyn <see cref="GeneratorAttributeSyntaxContext"/> into an immutable, value-equatable <see cref="ServiceModel"/>
/// or <see cref="HubReceiverModel"/>. Validates rules <c>HAYABUSA001</c> through <c>HAYABUSA005</c>.
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
        public HashSet<string> AddedMethodNames { get; } = new(StringComparer.Ordinal);
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

    public static HubReceiverModel ExtractHubReceiverModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var acc = new BuilderAccumulator();

        ImmutableArray<ISymbol> members = symbol.GetMembers();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
            {
                TryAddReceiverMethod(method, acc);
            }
        }

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();
        string fullType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new HubReceiverModel(
            (ns, symbol.Name, fullType),
            new EquatableArray<RpcMethodModel>(acc.RpcMethods.ToImmutable()),
            new EquatableArray<EquatableDiagnostic>(acc.Diagnostics.ToImmutable()));
    }

    private static void TryAddReceiverMethod(IMethodSymbol method, BuilderAccumulator acc)
    {
        AttributeData? rpcAttr = FindRpcAttribute(method.GetAttributes());
        int methodId = rpcAttr is not null
            ? ResolveMethodId(method, rpcAttr)
            : ComputeDeterministicMethodId(method.ContainingType.Name, method.Name);

        if (methodId <= 0 || !acc.SeenMethodIds.Add(methodId))
        {
            return;
        }

        if (!TryExtractParameters(method, acc, out EquatableArray<RpcParameterModel> parameters))
        {
            return;
        }

        acc.RpcMethods.Add(new RpcMethodModel(method.Name, methodId, parameters, (string.Empty, false, false)));
    }

    private static AttributeData? FindRpcAttribute(ImmutableArray<AttributeData> attrs)
    {
        for (int i = 0; i < attrs.Length; i++)
        {
            if (attrs[i].AttributeClass?.ToDisplayString() == RpcAttributeFullName)
            {
                return attrs[i];
            }
        }

        return null;
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
            if (members[i] is IMethodSymbol methodSymbol && methodSymbol.MethodKind == MethodKind.Ordinary)
            {
                InspectMethodSymbol(methodSymbol, acc);
            }
        }

        ParseMagicOnionContractMethods(serviceSymbol, acc);
    }

    private static void ParseMagicOnionContractMethods(INamedTypeSymbol serviceSymbol, BuilderAccumulator acc)
    {
        ImmutableArray<INamedTypeSymbol> interfaces = serviceSymbol.AllInterfaces;
        for (int i = 0; i < interfaces.Length; i++)
        {
            if (IsMagicOnionContractInterface(interfaces[i]))
            {
                AddUnattributedContractMethods(serviceSymbol, interfaces[i], acc);
            }
        }
    }

    private static bool IsMagicOnionContractInterface(INamedTypeSymbol iface)
    {
        ImmutableArray<INamedTypeSymbol> all = iface.AllInterfaces;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].Name is "IService" or "IStreamingHub")
            {
                return true;
            }
        }

        return false;
    }

    private static void AddUnattributedContractMethods(
        INamedTypeSymbol serviceSymbol,
        INamedTypeSymbol contractInterface,
        BuilderAccumulator acc)
    {
        ImmutableArray<ISymbol> ifaceMembers = contractInterface.GetMembers();
        for (int i = 0; i < ifaceMembers.Length; i++)
        {
            if (ifaceMembers[i] is IMethodSymbol ifaceMethod &&
                serviceSymbol.FindImplementationForInterfaceMember(ifaceMethod) is IMethodSymbol implMethod &&
                !acc.AddedMethodNames.Contains(implMethod.Name))
            {
                AttributeData? ifaceRpcAttr = FindRpcAttribute(ifaceMethod.GetAttributes());
                TryAddRpcMethod(implMethod, ifaceRpcAttr, acc);
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

    private static void TryAddRpcMethod(IMethodSymbol method, AttributeData? rpcAttr, BuilderAccumulator acc)
    {
        acc.AddedMethodNames.Add(method.Name);
        int methodId = rpcAttr is not null
            ? ResolveMethodId(method, rpcAttr)
            : ComputeDeterministicMethodId(method.ContainingType.Name, method.Name);

        Location? loc = GetSymbolFirstLocation(method);
        if (methodId <= 0 || !acc.SeenMethodIds.Add(methodId))
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.DuplicateOrInvalidMethodId.Id,
                EquatableSourceSpan.FromLocation(loc),
                (method.Name, methodId.ToString(CultureInfo.InvariantCulture), string.Empty)));
            return;
        }

        if (!TryValidateReturnType(method, acc, out (string FullName, bool HasPayload, bool IsAsync) resInfo) ||
            !TryExtractParameters(method, acc, out EquatableArray<RpcParameterModel> parameters))
        {
            return;
        }

        acc.RpcMethods.Add(new RpcMethodModel(method.Name, methodId, parameters, resInfo));
    }

    private static int ResolveMethodId(IMethodSymbol method, AttributeData rpcAttr)
    {
        if (TryExtractExplicitMethodId(rpcAttr, out int explicitId))
        {
            return explicitId;
        }

        return ComputeDeterministicMethodId(method.ContainingType.Name, method.Name);
    }

    private static bool TryExtractExplicitMethodId(AttributeData rpcAttr, out int methodId)
    {
        if (rpcAttr.ConstructorArguments.Length > 0 && rpcAttr.ConstructorArguments[0].Value is int ctorId)
        {
            methodId = ctorId;
            return true;
        }

        return TryFindNamedMethodId(rpcAttr.NamedArguments, out methodId);
    }

    private static bool TryFindNamedMethodId(
        ImmutableArray<KeyValuePair<string, TypedConstant>> named,
        out int methodId)
    {
        for (int i = 0; i < named.Length; i++)
        {
            if (named[i].Key == "MethodId" && named[i].Value.Value is int namedId)
            {
                methodId = namedId;
                return true;
            }
        }

        methodId = 0;
        return false;
    }

    private static int ComputeDeterministicMethodId(string serviceName, string methodName)
    {
        uint hash = HashStringFnv1a(2166136261u, serviceName);
        hash = (hash ^ '.') * 16777619u;
        hash = HashStringFnv1a(hash, methodName);
        int positive = (int)(hash & 0x7FFFFFFFu);
        return positive == 0 ? 1 : positive;
    }

    private static uint HashStringFnv1a(uint seed, string text)
    {
        uint hash = seed;
        for (int i = 0; i < text.Length; i++)
        {
            hash = (hash ^ text[i]) * 16777619u;
        }

        return hash;
    }

    private static bool TryValidateReturnType(
        IMethodSymbol method,
        BuilderAccumulator acc,
        out (string FullName, bool HasPayload, bool IsAsync) responseInfo)
    {
        responseInfo = (string.Empty, false, false);
        ITypeSymbol returnType = method.ReturnType;

        if (IsProhibitedAsyncReturnType(method, returnType))
        {
            acc.Diagnostics.Add(new EquatableDiagnostic(
                HayabusaDiagnostics.InvalidRpcReturnType.Id,
                EquatableSourceSpan.FromLocation(GetSymbolFirstLocation(method)),
                (method.Name, returnType.ToDisplayString(), string.Empty)));
            return false;
        }

        if (returnType.Name is "ValueTask" or "UniTask")
        {
            return TryExtractAsyncReturnPayload(method, returnType, acc) is { } valid &&
                   AssignOut(valid, out responseInfo);
        }

        return TryExtractSyncReturnPayload(method, returnType, acc, out responseInfo);
    }

    private static bool IsProhibitedAsyncReturnType(IMethodSymbol method, ITypeSymbol returnType) =>
        returnType.Name == "Task" || (method.IsAsync && returnType.SpecialType == SpecialType.System_Void);

    private static (string FullName, bool HasPayload, bool IsAsync)? TryExtractAsyncReturnPayload(
        IMethodSymbol method,
        ITypeSymbol returnType,
        BuilderAccumulator acc)
    {
        if (returnType is not INamedTypeSymbol { IsGenericType: true } genericReturn ||
            genericReturn.TypeArguments.Length != 1)
        {
            return (string.Empty, false, true);
        }

        ITypeSymbol innerType = genericReturn.TypeArguments[0];
        if (!IsMemoryPackCompatible(innerType))
        {
            ReportNotMemoryPackable(innerType, method, acc);
            return null;
        }

        return (innerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), true, true);
    }

    private static bool TryExtractSyncReturnPayload(
        IMethodSymbol method,
        ITypeSymbol returnType,
        BuilderAccumulator acc,
        out (string FullName, bool HasPayload, bool IsAsync) responseInfo)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            responseInfo = (string.Empty, false, false);
            return true;
        }

        if (!IsMemoryPackCompatible(returnType))
        {
            ReportNotMemoryPackable(returnType, method, acc);
            responseInfo = (string.Empty, false, false);
            return false;
        }

        responseInfo = (returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), true, false);
        return true;
    }

    private static bool AssignOut<T>(T value, out T target)
    {
        target = value;
        return true;
    }

    private static bool TryExtractParameters(
        IMethodSymbol method,
        BuilderAccumulator acc,
        out EquatableArray<RpcParameterModel> parameters)
    {
        ImmutableArray<IParameterSymbol> methodParams = method.Parameters;
        if (methodParams.Length == 0)
        {
            parameters = EquatableArray<RpcParameterModel>.Empty;
            return true;
        }

        ImmutableArray<RpcParameterModel>.Builder builder = ImmutableArray.CreateBuilder<RpcParameterModel>(methodParams.Length);
        for (int i = 0; i < methodParams.Length; i++)
        {
            IParameterSymbol param = methodParams[i];
            if (!IsMemoryPackCompatible(param.Type))
            {
                ReportNotMemoryPackable(param.Type, method, acc);
                parameters = EquatableArray<RpcParameterModel>.Empty;
                return false;
            }

            builder.Add(new RpcParameterModel(
                param.Name,
                param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        parameters = new EquatableArray<RpcParameterModel>(builder.ToImmutable());
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

        if (!TryValidateRouteTemplate(method, template, out (string Name, string TypeFullName) boundParam, out string errorReason))
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
        out (string Name, string TypeFullName) boundParam,
        out string errorReason)
    {
        boundParam = (string.Empty, string.Empty);
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
        out (string Name, string TypeFullName) boundParam,
        out string errorReason)
    {
        boundParam = (string.Empty, string.Empty);
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
        if (!TryFindParameterType(method, paramName, out string paramTypeFullName))
        {
            errorReason = $"Route parameter '{{{paramName}}}' does not match any method parameter.";
            return false;
        }

        boundParam = (paramName, paramTypeFullName);
        return true;
    }

    private static bool TryFindParameterType(IMethodSymbol method, string paramName, out string typeFullName)
    {
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (string.Equals(method.Parameters[i].Name, paramName, StringComparison.Ordinal))
            {
                typeFullName = method.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return true;
            }
        }

        typeFullName = string.Empty;
        return false;
    }

    private static Location? GetSymbolFirstLocation(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : null;
}
