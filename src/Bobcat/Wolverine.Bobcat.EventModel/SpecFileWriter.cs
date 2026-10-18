using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wolverine.Bobcat.EventModel;

/// <summary>
/// Writes the specification files to disk, the arm of Bobcat.EventModel's <see cref="ImportFileWriter"/> that moved
/// here with the spec writer (GH-5082). It reports into the same <see cref="ImportFileWriter"/> the stubs and
/// definitions were written through, so one run has one list of files, callouts and report lines, and it shares that
/// writer's index of each directory.
/// </summary>
/// <remarks>
/// A file that does not exist is written; with <c>--force</c> every file is overwritten; otherwise an example with
/// no specification gets one in the existing class bound to its feature, wherever that class is.
/// <c>TestSupport.cs</c> is never merged into, only written when missing; <c>GlobalUsings.cs</c> gains the
/// namespaces it lacks.
/// </remarks>
public sealed class SpecFileWriter(ImportFileWriter files, bool force)
{
    private static readonly CSharpParseOptions parse = new(LanguageVersion.Preview);

    public ImportWriteCounts WriteSpecs(string directory, IEnumerable<GeneratedFile> generated)
    {
        var index = files.IndexOf(directory);
        int written = 0, merged = 0, kept = 0;

        foreach (var file in generated)
        {
            var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(directory, relative);

            if (force)
            {
                // --force keeps its meaning: overwrite, as the import always did
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content); // UTF-8 without a BOM
                files.Files.Add(new ImportedFile(path, "write"));
                written++;
                continue;
            }

            switch (writeOne(directory, file, path, relative, index))
            {
                case Outcome.Written:
                    files.Files.Add(new ImportedFile(path, "write"));
                    written++;
                    break;
                case Outcome.Merged:
                case Outcome.MergedElsewhere:
                    // mergeInto() recorded the file it wrote, which may be another than this path
                    merged++;
                    break;
                case Outcome.Kept:
                    files.Files.Add(new ImportedFile(path, "kept"));
                    kept++;
                    break;
            }
        }

        return new ImportWriteCounts(written, merged, kept);
    }

    private Outcome writeOne(string directory, GeneratedFile file, string path, string relative, SourceIndex index)
    {
        if (file.Path == EmlangSpecWriter.TestSupportFile)
        {
            return File.Exists(path) ? Outcome.Kept : write(path, file.Content, index);
        }

        if (file.Path == EmlangSpecWriter.GlobalUsingsFile)
        {
            return File.Exists(path)
                ? mergeInto(path, relative, SpecMerge.MergeGlobalUsings(File.ReadAllText(path), file.Content, path), index, "global using", "global usings")
                : write(path, file.Content, index);
        }

        var features = CSharpSyntaxTree.ParseText(file.Content, parse).GetCompilationUnitRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(ImportMerge.FeatureOf).OfType<string>().ToList();

        // Into the class bound to the feature, which may have been moved to another file
        var holders = features.SelectMany(index.FilesWithFeature).Distinct(StringComparer.Ordinal).ToList();
        var target = holders.Contains(Path.GetFullPath(path)) ? Path.GetFullPath(path) : holders.Count == 1 ? holders[0] : null;

        if (target is null && holders.Count > 1)
        {
            files.Callouts.Add(new ImportCallout(features.FirstOrDefault() ?? "", ImportCalloutCodes.MergeSkippedAmbiguous, [],
                $"{holders.Count} files hold a class bound to [BobcatFeature(\"{features.FirstOrDefault()}\")] "
                + $"({string.Join(", ", holders.Select(x => Path.GetRelativePath(directory, x)))}), so where {relative}'s new specifications go is ambiguous.", null)
                { File = path });
            return File.Exists(path) ? Outcome.Kept : Outcome.Skipped;
        }

        if (target is null)
        {
            if (!File.Exists(path)) return write(path, file.Content, index);
            target = Path.GetFullPath(path);
        }

        var result = SpecMerge.MergeSpecs(File.ReadAllText(target), file.Content, target, index);
        var outcome = mergeInto(target, Path.GetRelativePath(directory, target), result, index, "specification", "specifications");

        // Merged into another file: this path itself was never written
        return target == Path.GetFullPath(path) || outcome != Outcome.Merged ? outcome : Outcome.MergedElsewhere;
    }

    private enum Outcome
    {
        Written,
        Merged,
        MergedElsewhere,
        Kept,
        Skipped
    }

    private static Outcome write(string path, string content, SourceIndex index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); // UTF-8 without a BOM
        index.Update(path, content);
        return Outcome.Written;
    }

    private Outcome mergeInto(string path, string relative, ImportMerge.Result result, SourceIndex index, string singular, string plural)
    {
        files.Callouts.AddRange(result.Callouts);
        if (result.Content is null) return Outcome.Kept;

        File.WriteAllText(path, result.Content);
        index.Update(path, result.Content);
        files.Files.Add(new ImportedFile(path, "merge"));
        files.Lines.Add(result.Description is { } description
            ? $"Added {description} to {relative}."
            : $"Added {result.Added.Count} {(result.Added.Count == 1 ? singular : plural)} to {relative}: {string.Join(", ", result.Added)}.");
        return Outcome.Merged;
    }
}
