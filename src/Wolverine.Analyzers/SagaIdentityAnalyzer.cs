using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wolverine.Analyzers;

/// <summary>
/// WOLV0002: [SagaIdentityFrom("X")] on a saga handler parameter whose type has no public field or property X.
/// </summary>
/// <remarks>
/// A saga message with no identity member at all is NOT reported: Wolverine reads the identity from the SagaId
/// envelope header then, which is how a saga's own timeout messages and the replies to the messages it sends work.
/// Telling that apart from a mistake takes knowing who sends the message, so the rule only reports the case that
/// is wrong whoever sends it.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SagaIdentityAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticRules.SagaIdentityMemberNotFound);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var symbols = WolverineSymbols.TryCreate(start.Compilation);
            if (symbols?.SagaIdentityFromAttribute == null || symbols.Saga == null) return;

            start.RegisterSymbolAction(x => analyzeMethod(x, symbols), SymbolKind.Method);
        });
    }

    private static void analyzeMethod(SymbolAnalysisContext context, WolverineSymbols symbols)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.Parameters.Length == 0) return;

        foreach (var parameter in method.Parameters)
        {
            foreach (var attribute in parameter.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.SagaIdentityFromAttribute))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not string memberName ||
                    memberName.Length == 0)
                {
                    continue;
                }

                if (parameter.Type is ITypeParameterSymbol || parameter.Type.TypeKind == TypeKind.Error) continue;

                if (hasPublicFieldOrProperty(parameter.Type, memberName)) continue;

                var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                               ?? parameter.Locations.FirstOrDefault()
                               ?? method.Locations[0];

                context.ReportDiagnostic(Diagnostic.Create(DiagnosticRules.SagaIdentityMemberNotFound, location,
                    memberName, parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }
    }

    // Type.GetFields() + Type.GetProperties(): public instance and static members, inherited ones included
    private static bool hasPublicFieldOrProperty(ITypeSymbol type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member.DeclaredAccessibility == Accessibility.Public &&
                    member.Kind is SymbolKind.Field or SymbolKind.Property)
                {
                    return true;
                }
            }
        }

        // An interface message type: Type.GetProperties() on an interface only sees its own members, but be
        // generous and look at the inherited interfaces too rather than report something Wolverine may find
        foreach (var @interface in type.AllInterfaces)
        {
            if (@interface.GetMembers(name).Any(m => m.Kind == SymbolKind.Property)) return true;
        }

        return false;
    }
}
