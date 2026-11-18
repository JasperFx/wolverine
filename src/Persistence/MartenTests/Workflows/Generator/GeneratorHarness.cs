using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Wolverine.SourceGeneration;

namespace MartenTests.Workflows.Generator;

/// <summary>
/// GH-5660 SPIKE. Runs Wolverine.SourceGeneration over a snippet, compiled against everything this test process can
/// see (Wolverine, and the workflow runtime in this assembly)
/// </summary>
public static class GeneratorHarness
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray());

    public const string Messages = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Wolverine.Persistence.Sagas;
        using Wolverine.Workflows;

        namespace Spike;

        public record Go([property: SagaIdentity] Guid Id, int Count);
        public record Ask(Guid Id);
        public record Answer(string Text);
        public record Approved([property: SagaIdentity] Guid Id, string By);
        public record Note(string Text);

        """;

    /// <summary>A workflow method with <paramref name="body" /> as its body, in a class with a few helpers</summary>
    public static string Workflow(string body, string modifiers = "public static")
    {
        return Messages + $$"""
            public class Flow
            {
                [DurableWorkflow]
                {{modifiers}} async Task Run(Go go, IWorkflowContext ctx)
                {
            {{body}}
                }

                public const int Limit = 10;
                public static void Use(object? value) { }
                private static int secret() => 1;
            }
            """;
    }

    public static GeneratorRun Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Flow.cs");
        var compilation = CSharpCompilation.Create("WorkflowSubject", [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DurableWorkflowGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var result = driver.GetRunResult().Results.Single();
        return new GeneratorRun(compilation, output, result.Diagnostics.ToArray(),
            result.GeneratedSources.Select(x => x.SourceText.ToString()).ToArray());
    }
}

public record GeneratorRun(Compilation Input, Compilation Output, Diagnostic[] Diagnostics, string[] Generated)
{
    public string[] Ids => Diagnostics.Select(x => x.Id).Distinct().ToArray();

    /// <summary>The snippet compiles on its own, so a refusal comes from the generator and not from C#</summary>
    public GeneratorRun ShouldCompileBeforeGeneration()
    {
        Input.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString()).ShouldBeEmpty();
        return this;
    }

    public GeneratorRun ShouldBeRefusedWith(string id)
    {
        ShouldCompileBeforeGeneration();
        Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.Id).Distinct()
            .ShouldBe([id], string.Join(Environment.NewLine, Diagnostics.Select(x => x.ToString())));
        Generated.ShouldBeEmpty();
        return this;
    }

    /// <summary>A saga was generated, and the project still compiles with it</summary>
    public string ShouldGenerateASaga()
    {
        ShouldCompileBeforeGeneration();
        Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.ToString()).ShouldBeEmpty();
        Generated.Length.ShouldBe(1);

        // Any error, and any warning in the generated file
        Output.GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error ||
                        (x.Severity == DiagnosticSeverity.Warning && x.Location.SourceTree?.FilePath != "Flow.cs"))
            .Select(x => x.ToString()).ShouldBeEmpty(Generated[0]);

        return Generated[0];
    }
}
