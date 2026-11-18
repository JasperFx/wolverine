using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Wolverine.SourceGeneration;

/// <summary>
/// GH-5660 SPIKE. Finds every static [DurableWorkflow] method and generates a Wolverine saga from it: each await on an
/// IWorkflowContext member that waits for a message (InvokeAsync, WaitForAsync, DelayAsync) ends one saga step.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DurableWorkflowGenerator : IIncrementalGenerator
{
    public const string AttributeName = "Wolverine.Workflows.DurableWorkflowAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Everything is worked out in the transform, and only strings and plain values come out of it, so the
        // pipeline caches on them: an edit elsewhere in the file re-runs the transform but not the output step
        var workflows = context.SyntaxProvider.ForAttributeWithMetadataName(AttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, token) => WorkflowSplitter.Generate((MethodDeclarationSyntax)ctx.TargetNode,
                (IMethodSymbol)ctx.TargetSymbol, ctx.SemanticModel, token));

        context.RegisterSourceOutput(workflows, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (result.Source != null)
            {
                spc.AddSource(result.HintName!, result.Source);
            }
        });
    }
}

/// <summary>A diagnostic held as plain values, so the incremental pipeline can compare it</summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params string[] args)
    {
        Id = descriptor.Id;
        Args = args;
        Path = location.SourceTree?.FilePath ?? "";
        Span = location.SourceSpan;
        LineSpan = location.GetLineSpan().Span;
    }

    public string Id { get; }
    public string[] Args { get; }
    public string Path { get; }
    public TextSpan Span { get; }
    public LinePositionSpan LineSpan { get; }

    public Diagnostic ToDiagnostic()
    {
        // ReSharper disable once CoVariantArrayConversion
        return Diagnostic.Create(WorkflowDiagnostics.Find(Id)!, Location.Create(Path, Span, LineSpan), Args);
    }

    public bool Equals(DiagnosticInfo? other)
    {
        return other != null && Id == other.Id && Path == other.Path && Span == other.Span &&
               Args.SequenceEqual(other.Args);
    }

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode() => (Id, Path, Span).GetHashCode();
}

/// <summary>What the generator produced for one workflow method: a saga, or the reasons it couldn't make one</summary>
internal sealed class GenerationResult : IEquatable<GenerationResult>
{
    public GenerationResult(string? hintName, string? source, IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        HintName = hintName;
        Source = source;
        Diagnostics = diagnostics;
    }

    public string? HintName { get; }
    public string? Source { get; }
    public IReadOnlyList<DiagnosticInfo> Diagnostics { get; }

    public bool Equals(GenerationResult? other)
    {
        return other != null && HintName == other.HintName && Source == other.Source &&
               Diagnostics.SequenceEqual(other.Diagnostics);
    }

    public override bool Equals(object? obj) => Equals(obj as GenerationResult);

    public override int GetHashCode() => (HintName, Source?.Length ?? 0, Diagnostics.Count).GetHashCode();
}
