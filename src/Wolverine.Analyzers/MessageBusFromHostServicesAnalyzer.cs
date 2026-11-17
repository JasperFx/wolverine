using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wolverine.Analyzers;

/// <summary>
/// WOLV0004: host.Services.GetRequiredService&lt;IMessageBus&gt;() (or GetService, or the typeof(IMessageBus) forms) in
/// a test project. Only the direct form on an IHost's Services property is reported.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MessageBusFromHostServicesAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> TestFrameworkAssemblies = ImmutableHashSet.Create(
        System.StringComparer.OrdinalIgnoreCase,
        "xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "MSTest.TestFramework", "TUnit.Core");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticRules.MessageBusFromHostServices);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            var messageBus = compilation.GetTypeByMetadataName("Wolverine.IMessageBus");
            var host = compilation.GetTypeByMetadataName("Microsoft.Extensions.Hosting.IHost");
            if (messageBus == null || host == null) return;

            if (!compilation.ReferencedAssemblyNames.Any(x => TestFrameworkAssemblies.Contains(x.Name))) return;

            var hostServices = host.GetMembers("Services").OfType<IPropertySymbol>().FirstOrDefault();
            if (hostServices == null) return;

            start.RegisterOperationAction(x => analyzeInvocation(x, messageBus, hostServices),
                OperationKind.Invocation);
        });
    }

    private static void analyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol messageBus,
        IPropertySymbol hostServices)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (method.Name is not ("GetRequiredService" or "GetService")) return;
        if (!resolvesMessageBus(invocation, messageBus)) return;

        var provider = method.IsExtensionMethod || method.ReducedFrom != null
            ? invocation.Arguments.FirstOrDefault()?.Value
            : invocation.Instance;

        if (provider == null || !isHostServices(unwrapConversions(provider), hostServices)) return;

        context.ReportDiagnostic(Diagnostic.Create(DiagnosticRules.MessageBusFromHostServices,
            invocation.Syntax.GetLocation()));
    }

    private static bool resolvesMessageBus(IInvocationOperation invocation, INamedTypeSymbol messageBus)
    {
        var method = invocation.TargetMethod;
        if (method.IsGenericMethod)
        {
            return method.TypeArguments.Length == 1 &&
                   SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], messageBus);
        }

        // GetService(typeof(IMessageBus)) / GetRequiredService(typeof(IMessageBus))
        return invocation.Arguments.Any(a =>
            unwrapConversions(a.Value) is ITypeOfOperation typeOf &&
            SymbolEqualityComparer.Default.Equals(typeOf.TypeOperand, messageBus));
    }

    private static bool isHostServices(IOperation operation, IPropertySymbol hostServices)
    {
        if (operation is not IPropertyReferenceOperation propertyReference) return false;

        var property = propertyReference.Property;
        if (SymbolEqualityComparer.Default.Equals(property.OriginalDefinition, hostServices)) return true;

        // WebApplication and other IHost implementations declare their own Services property
        var implementation = property.ContainingType.FindImplementationForInterfaceMember(hostServices);
        return SymbolEqualityComparer.Default.Equals(implementation, property);
    }

    private static IOperation unwrapConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }
}
