using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wolverine.Analyzers;

/// <summary>
/// WOLV0001 (non-public handler type or method) and WOLV0003 (Task or ValueTask in a cascading tuple)
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerConventionsAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticRules.HandlerIsNotPublic, DiagnosticRules.CascadingTupleContainsTask);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var symbols = WolverineSymbols.TryCreate(start.Compilation);
            if (symbols == null) return;

            start.RegisterSymbolAction(x => analyzeType(x, symbols), SymbolKind.NamedType);
        });
    }

    private static void analyzeType(SymbolAnalysisContext context, WolverineSymbols symbols)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (symbols.IsConventionalHandlerType(type))
        {
            analyzeHandlerType(context, symbols, type);
        }
        else if (symbols.HttpMethodAttribute != null && type.TypeKind == TypeKind.Class)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(symbols.IsHttpEndpoint))
            {
                checkCascadingTuple(context, symbols, method);
            }
        }
    }

    private static void analyzeHandlerType(SymbolAnalysisContext context, WolverineSymbols symbols,
        INamedTypeSymbol type)
    {
        var isSaga = symbols.IsSaga(type);
        var candidates = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => symbols.IsHandlerMethodCandidate(m, isSaga))
            .ToArray();

        foreach (var method in candidates.Concat(type.GetMembers().OfType<IMethodSymbol>().Where(symbols.IsHttpEndpoint))
                     .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
        {
            checkCascadingTuple(context, symbols, method);
        }

        // WOLV0001 only for methods that look like a message handler for another framework's contract: an
        // override or an interface implementation (MediatR, MassTransit, NServiceBus) is not meant for Wolverine
        var wolverineCandidates = candidates
            .Where(m => !m.IsOverride && !implementsInterfaceMember(type, m) && hasPlausibleMessageType(m))
            .ToArray();

        if (wolverineCandidates.Length == 0) return;

        // Same test as HandlerDiscovery.isNotPublicType: a top level type must be public, a nested type must be
        // declared public (whatever its containing type is)
        if (type.DeclaredAccessibility != Accessibility.Public)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticRules.HandlerIsNotPublic, type.Locations[0],
                $"Handler type '{type.Name}'"));
            return;
        }

        foreach (var method in wolverineCandidates.Where(m => m.DeclaredAccessibility != Accessibility.Public))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticRules.HandlerIsNotPublic, method.Locations[0],
                $"Method '{type.Name}.{method.Name}'"));
        }
    }

    private static void checkCascadingTuple(SymbolAnalysisContext context, WolverineSymbols symbols,
        IMethodSymbol method)
    {
        if (method.ReturnsVoid) return;

        var returned = symbols.UnwrapAsync(method.ReturnType);
        if (returned is not INamedTypeSymbol { IsTupleType: true } tuple) return;

        var taskElement = tuple.TupleElements.FirstOrDefault(e => symbols.IsTaskLike(e.Type));
        if (taskElement == null) return;

        var location = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax syntax
            ? syntax.ReturnType.GetLocation()
            : method.Locations[0];

        context.ReportDiagnostic(Diagnostic.Create(DiagnosticRules.CascadingTupleContainsTask, location,
            $"'{method.ContainingType.Name}.{method.Name}'",
            taskElement.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static bool implementsInterfaceMember(INamedTypeSymbol type, IMethodSymbol method)
    {
        if (!method.ExplicitInterfaceImplementations.IsEmpty) return true;

        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers(method.Name))
            {
                var implementation = type.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, method)) return true;
            }
        }

        return false;
    }

    // The first parameter is the message. Framework types (an Exception, an HttpContext, a CancellationToken) and
    // primitives mean a helper method that only shares a name with a handler, so the rule stays quiet about it
    private static bool hasPlausibleMessageType(IMethodSymbol method)
    {
        var messageType = method.Parameters[0].Type;
        if (messageType.SpecialType != SpecialType.None) return false;
        if (messageType.TypeKind is TypeKind.TypeParameter or TypeKind.Pointer or TypeKind.Dynamic) return false;

        var root = rootNamespace(messageType);
        return root != "System" && root != "Microsoft";
    }

    private static string? rootNamespace(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        if (ns == null || ns.IsGlobalNamespace) return null;
        while (!ns.ContainingNamespace.IsGlobalNamespace) ns = ns.ContainingNamespace;
        return ns.Name;
    }
}
