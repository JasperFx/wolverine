using System.Text;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wolverine.Bobcat.EventModel;

/// <summary>
/// The specification arm of Bobcat.EventModel's additive re-import (<c>ImportMerge</c>), which moved here with the spec
/// writer (GH-5082, JasperFx/bobcat#744): a re-run adds the board's new examples to the spec project and changes
/// nothing already there. Bobcat.EventModel keeps merging stubs and definitions; the small syntax helpers at the end
/// are copies of its private ones, so the two arms insert text the same way.
/// </summary>
public static class SpecMerge
{
    private static readonly CSharpParseOptions parse = new(LanguageVersion.Preview);

    /// <summary>
    /// Add the specifications <paramref name="generated"/>'s feature class has that no class bound to the
    /// same <c>[BobcatFeature]</c> in the spec project has, at the end of that class. Existing
    /// specification methods are never touched. The same rule <see cref="EmlangSpecWriter.MissingSpecs"/>
    /// reports by: a feature and a method name.
    /// </summary>
    /// <param name="existing">The file holding the feature class, as it is.</param>
    /// <param name="generated">The specification file a fresh import would write.</param>
    /// <param name="path">The existing file's full path.</param>
    /// <param name="index">Every feature class and specification method the spec project declares.</param>
    public static ImportMerge.Result MergeSpecs(string existing, string generated, string path, SourceIndex index)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return ImportMerge.Result.Unchanged(unparseable(path, error));

        var fresh = CSharpSyntaxTree.ParseText(generated, parse).GetCompilationUnitRoot();
        var root = tree.GetCompilationUnitRoot();
        var text = existing;
        var newline = newlineOf(text);
        var edits = new List<(int, string)>();
        var added = new List<string>();
        var callouts = new List<ImportCallout>();

        foreach (var feature in fresh.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (ImportMerge.FeatureOf(feature) is not { } name) continue;

            var missing = feature.Members.OfType<MethodDeclarationSyntax>()
                .Where(x => !index.HasSpec(name, x.Identifier.ValueText))
                .ToList();
            if (missing.Count == 0) continue;

            var targets = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(x => ImportMerge.FeatureOf(x) == name).ToList();
            if (targets.Count != 1)
            {
                callouts.Add(targets.Count == 0
                    ? noInsertionPoint(path, $"{Path.GetFileName(path)} has no class bound to [BobcatFeature(\"{name}\")], so {string.Join(", ", missing.Select(x => x.Identifier.ValueText))} could not be added. Copy {(missing.Count == 1 ? "it" : "them")} from a fresh import (--specs-out to an empty directory).") with { Slice = name }
                    : ambiguous(path, $"{Path.GetFileName(path)} has {targets.Count} classes bound to [BobcatFeature(\"{name}\")], so where the new specifications go is ambiguous.") with { Slice = name });
                continue;
            }

            var target = targets[0];
            if (target.OpenBraceToken.IsMissing || target.OpenBraceToken.Span.Length == 0)
            {
                callouts.Add(noInsertionPoint(path, $"The class bound to [BobcatFeature(\"{name}\")] has no body to add specifications to.") with { Slice = name });
                continue;
            }

            var block = string.Join("\n\n", missing.Select(x => Reindent(x.ToFullString(), "")));
            if (target.Members.Count > 0)
            {
                var indent = IndentOf(text, target.Members[0].SpanStart);
                edits.Add(insertion(text, target.Members.Last().FullSpan.End, Reindent(block, indent), newline, blankBefore: true));
            }
            else
            {
                var indent = IndentOf(text, target.SpanStart) + "    ";
                edits.Add(insertion(text, target.OpenBraceToken.FullSpan.End, Reindent(block, indent), newline, blankBefore: false));
            }

            foreach (var method in missing)
            {
                var spec = $"{name} / {method.Identifier.ValueText}";
                added.Add(spec);
                callouts.Add(callout(ImportCalloutCodes.MergedSpec, path,
                    $"Added the specification {method.Identifier.ValueText} to {target.Identifier.ValueText} in {Path.GetFileName(path)}: the board has the example and the spec project had no specification for it.") with { Slice = name });
            }
        }

        return new ImportMerge.Result(edits.Count == 0 ? null : apply(text, edits), added, callouts);
    }

    /// <summary>The <c>global using</c> directives <paramref name="generated"/> has that <paramref name="existing"/> lacks, added after its last.</summary>
    public static ImportMerge.Result MergeGlobalUsings(string existing, string generated, string path)
    {
        var tree = TryParse(existing, out var error);
        if (tree is null) return ImportMerge.Result.Unchanged(unparseable(path, error));

        var root = tree.GetCompilationUnitRoot();
        var have = root.Usings.Where(x => x.GlobalKeyword.Span.Length > 0).Select(x => x.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var fresh = CSharpSyntaxTree.ParseText(generated, parse).GetCompilationUnitRoot();
        var missing = fresh.Usings.Select(x => x.Name?.ToString()).OfType<string>().Where(x => !have.Contains(x)).ToList();
        if (missing.Count == 0) return ImportMerge.Result.Unchanged();

        var newline = newlineOf(existing);
        var block = string.Join("\n", missing.Select(x => $"global using {x};"));
        var position = root.Usings.Count > 0 ? root.Usings.Last().FullSpan.End : root.EndOfFileToken.FullSpan.Start;
        var content = apply(existing, [insertion(existing, position, block, newline, blankBefore: false)]);

        return new ImportMerge.Result(content, missing, missing.Select(x => callout(ImportCalloutCodes.MergedUsing, path,
            $"Added global using {x} to {Path.GetFileName(path)}: the stubs now declare types there.")).ToList());
    }

    // ---------------------------------------------------------------- syntax helpers, as ImportMerge has them

    private static SyntaxTree? TryParse(string text, out string? error)
    {
        var tree = CSharpSyntaxTree.ParseText(text, parse);
        var first = tree.GetDiagnostics().FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
        error = first is null ? null : $"{first.Id} at line {first.Location.GetLineSpan().StartLinePosition.Line + 1}: {first.GetMessage()}";
        return first is null ? tree : null;
    }

    private static string Reindent(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var common = lines.Where(x => x.Length > 0).Select(x => x.TakeWhile(char.IsWhiteSpace).Count()).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(x => x.Length == 0 ? "" : indent + x[common..]));
    }

    private static string IndentOf(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        return new string(text.Skip(lineStart).Take(position - lineStart).TakeWhile(c => c is ' ' or '\t').ToArray());
    }

    private static string newlineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>
    /// The text to insert at <paramref name="position"/>, which is normally the start of a line: the
    /// block on lines of its own, after a blank line when <paramref name="blankBefore"/>.
    /// </summary>
    private static (int, string) insertion(string text, int position, string block, string newline, bool blankBefore)
    {
        var builder = new StringBuilder();
        var atLineStart = position == 0 || text[position - 1] == '\n';
        if (!atLineStart) builder.Append('\n');
        if (blankBefore) builder.Append('\n');
        builder.Append(block);
        builder.Append('\n');

        // A block written at the very end of a file with no final newline keeps the file's last line its own
        return (position, builder.ToString().Replace("\n", newline));
    }

    /// <summary>Apply insertions, last first, so every offset still means what Roslyn said.</summary>
    private static string apply(string text, IReadOnlyList<(int Position, string Text)> edits)
    {
        var builder = new StringBuilder(text);
        foreach (var (position, inserted) in edits.OrderByDescending(x => x.Position)) builder.Insert(position, inserted);
        return builder.ToString();
    }

    private static ImportCallout callout(string code, string path, string message) => new("", code, [], message, null) { File = path };

    private static ImportCallout unparseable(string path, string? error)
        => callout(ImportCalloutCodes.MergeSkippedUnparseable, path,
            $"{Path.GetFileName(path)} does not parse ({error}), so nothing was merged into it. Fix it and import again.");

    private static ImportCallout noInsertionPoint(string path, string message) => callout(ImportCalloutCodes.MergeSkippedNoInsertionPoint, path, message);

    private static ImportCallout ambiguous(string path, string message) => callout(ImportCalloutCodes.MergeSkippedAmbiguous, path, message);
}
