using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Wolverine.Analyzers;
using Xunit;

namespace CoreTests.Analyzers;

/// <summary>
/// Compiles a snippet against everything this test process can see (Wolverine, the hosting abstractions, xUnit) and
/// runs WolverineFx.Analyzers over it
/// </summary>
public static class AnalyzerHarness
{
    private static readonly string[] TestFrameworks = ["xunit.core", "xunit.v3.core"];

    private static readonly Lazy<MetadataReference[]> AllReferences = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray());

    private static readonly Lazy<MetadataReference[]> ReferencesWithoutTestFrameworks = new(() =>
        AllReferences.Value
            .Where(x => !TestFrameworks.Contains(Path.GetFileNameWithoutExtension(x.Display), StringComparer.OrdinalIgnoreCase))
            .ToArray());

    public static async Task<Diagnostic[]> DiagnosticsFor(string source, bool asTestProject = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var references = asTestProject ? AllReferences.Value : ReferencesWithoutTestFrameworks.Value;

        var compilation = CSharpCompilation.Create("AnalyzerSubject", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // The snippet itself has to compile, or a missing diagnostic proves nothing
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new HandlerConventionsAnalyzer(),
            new SagaIdentityAnalyzer(), new MessageBusFromHostServicesAnalyzer());

        var diagnostics = await compilation.WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return diagnostics.OrderBy(x => x.Location.SourceSpan.Start).ToArray();
    }

    public static string Describe(this Diagnostic diagnostic)
    {
        var span = diagnostic.Location.SourceSpan;
        var text = diagnostic.Location.SourceTree!.GetText().ToString(span);
        return $"{diagnostic.Id}: {text}";
    }
}
