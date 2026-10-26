using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using JasperFx.CommandLine;

namespace Wolverine.Bobcat.EventModel;

public class WriteSpecsInput
{
    [Description("An eventmodelers.ai export (a board backup or config.json), or an emlang model (YAML)")]
    public string FilePath { get; set; } = string.Empty;

    [Description("Model name; defaults to the file name. It is the merge key, so it must match what the code-derived sources call the model")]
    [FlagAlias("model", 'm')]
    public string? ModelFlag { get; set; }

    [Description("Namespace of the stubs and definition, as bobcat import-event-model wrote them; defaults to the model name")]
    public string? NamespaceFlag { get; set; }

    [Description("The application project's directory, where bobcat import-event-model wrote the stubs (Features/{Chapter}/{Slice}.cs) and the definitions. The members the specifications need are merged into those stubs. Defaults beside the input")]
    [FlagAlias("out", 'o')]
    public string? OutFlag { get; set; }

    [Description("Directory the specification files are written to; defaults to a sibling of --out named for the spec namespace, e.g. ../CritterCrush.Specs")]
    [FlagAlias("specs-out", true)]
    public string? SpecsOutFlag { get; set; }

    [Description("Root namespace of the specifications, which should be the spec project's name; defaults to '<namespace>.Specs'. Each chapter folder adds a segment")]
    [FlagAlias("specs-namespace", true)]
    public string? SpecsNamespaceFlag { get; set; }

    [Description("WolverineFx.Bobcat version for a spec project this writes (one is written when the spec directory has no .csproj); defaults to the WolverineFx.Bobcat built alongside this package. The csproj keeps it in the WolverineFxBobcatVersion property")]
    [FlagAlias("wolverine-version", true)]
    public string? WolverineVersionFlag { get; set; }

    [Description("Bobcat package version for a spec project this writes; defaults to the Bobcat this package was built against")]
    [FlagAlias("bobcat-version", true)]
    public string? BobcatVersionFlag { get; set; }

    [Description("The application's .csproj, which the spec project references and the checks on Program read; defaults to the one .csproj in --out")]
    [FlagAlias("app-project", true)]
    public string? AppProjectFlag { get; set; }

    [Description("The application's event store -- marten, polecat or fisher -- which decides how the generated test host runs its async daemon")]
    [FlagAlias("store", true)]
    public string? StoreFlag { get; set; }

    [Description("The stubs carry one EventModelDefinition for the whole model instead of one per chapter (as bobcat import-event-model --single-definition wrote them)")]
    [FlagAlias("single-definition", true)]
    public bool SingleDefinitionFlag { get; set; }

    [Description("The stubs give each aggregate a strong-typed identifier (as bobcat import-event-model --strong-typed-ids wrote them), so the specifications declare identities as those types")]
    [FlagAlias("strong-typed-ids", true)]
    public bool StrongTypedIdsFlag { get; set; }

    [Description("The aggregate a slice decides against, as Slice=Type, which wins over the one inferred from the examples. The same overrides bobcat import-event-model was given")]
    [FlagAlias("aggregate", true)]
    public List<string> AggregateFlag { get; set; } = [];

    [Description("The aggregate whose stream a slice STARTS, as Slice=Type, which wins over the inference. The same overrides bobcat import-event-model was given")]
    [FlagAlias("starts-stream", true)]
    public List<string> StartsStreamFlag { get; set; } = [];

    [Description("Write specifications for only these board chapters, space-separated: --chapter A B")]
    [FlagAlias("chapter", true)]
    public List<string> ChapterFlag { get; set; } = [];

    [Description("Write the run to stdout as JSON -- every callout with its stable code, the report, and every file -- for tooling such as Stoat, instead of the text report")]
    [FlagAlias("json", true)]
    public bool JsonFlag { get; set; }

    [Description("Overwrite specification and stub files that already exist. Without it an existing file is merged into: the board's new specifications and members are added and nothing already there is changed. The spec project's own files are never overwritten")]
    public bool ForceFlag { get; set; }
}

/// <summary>
/// <c>write-specs &lt;board&gt;</c>: one WolverineFx.Bobcat specification per example on an eventmodelers.ai board,
/// bound to its Event Model slice, and the spec project around them when the spec directory has none. This is what
/// <c>bobcat import-event-model --specs</c> did until the spec writer moved to the Wolverine repository with the
/// Critter Stack vocabulary the specifications are written against (GH-5082, JasperFx/bobcat#744).
/// </summary>
/// <remarks>
/// <para>
/// <b>The workflow:</b> <c>bobcat import-event-model board.json --out App</c> writes the stubs and the
/// <c>EventModelDefinition</c>s; then this command, run with the same board and the same import flags, writes the
/// specifications. A generated specification can name a member the board gives only in an example, so the stubs
/// gain those members here, through the same additive merge the import uses: nothing in them is changed or removed,
/// and running it twice changes nothing the second time. Run alone, it writes the stubs and definitions too, exactly
/// as the old <c>--specs</c> import did.
/// </para>
/// <para>
/// <b>It runs in the application</b>, next to <c>scaffold</c>: an application referencing WolverineFx.Bobcat.EventModel
/// gets it from <c>dotnet run -- write-specs</c>. It never builds or starts the host; everything it reads is the
/// board and the files on disk.
/// </para>
/// </remarks>
[Description("Write a WolverineFx.Bobcat specification for every example on an eventmodelers.ai board, and the spec project around them",
    Name = "write-specs")]
public class WriteSpecsCommand : JasperFxAsyncCommand<WriteSpecsInput>
{
    public WriteSpecsCommand()
    {
        Usage("Write the specifications for a board export").Arguments(x => x.FilePath);
    }

    public override async Task<bool> Execute(WriteSpecsInput input)
    {
        if (!File.Exists(input.FilePath))
        {
            await Console.Error.WriteLineAsync($"No file at {input.FilePath}");
            return false;
        }

        var text = await File.ReadAllTextAsync(input.FilePath);

        // With --json nothing but the JSON reaches stdout, so a tool can parse it whole
        var run = new SpecRun(input.JsonFlag ? TextWriter.Null : Console.Out);

        var model = EventModelFileSniffer.Sniff(text) switch
        {
            EventModelFileKind.Emlang or EventModelFileKind.EventModelersBoard or EventModelFileKind.EventModelersConfig
                => write(input, text, run),
            EventModelFileKind.Curated => refuse(
                $"{input.FilePath} is a curated event-model file, which is no longer an authoring format. Specifications are "
                + "written from the eventmodelers.ai board export the model was imported from."),
            _ => refuse(
                $"{input.FilePath} is not an emlang file. Expected a top-level `slices:` map, each slice a list of steps or a map "
                + "of `steps:` and `tests:`. (An eventmodelers.ai export is a board backup or a config.json.)")
        };

        if (model is null) return false;

        if (input.JsonFlag)
        {
            await Console.Out.WriteLineAsync(ImportJson.Write(model, run.Callouts, run.Report, run.Files));
        }

        return true;
    }

    /// <summary>What one run reports: the text, or with --json the callouts, report and files the JSON carries.</summary>
    private sealed class SpecRun(TextWriter output)
    {
        public TextWriter Out { get; } = output;
        public IReadOnlyList<ImportCallout> Callouts { get; set; } = [];
        public List<string> Report { get; } = [];
        public List<ImportedFile> Files { get; set; } = [];

        public void Warn(IEnumerable<ImportCallout> callouts)
        {
            foreach (var callout in callouts)
            {
                Out.WriteLine($"⚠ [{callout.Code}] {callout.Message}");
                if (callout.Fix is not null) Out.WriteLine($"    {callout.Fix.Replace("\n", "\n    ")}");
            }
        }
    }

    private static string? refuse(string message)
    {
        Console.Error.WriteLine(message);
        return null;
    }

    private static string? write(WriteSpecsInput input, string text, SpecRun run)
    {
        EmlangBoard board;
        try
        {
            board = EventModelersJsonReader.LooksLikeJson(text) ? EventModelersJsonReader.Read(text) : EmlangReader.Read(text);
        }
        catch (EmlangFormatException e)
        {
            Console.Error.WriteLine($"Not readable: {e.Message}");
            return null;
        }

        var model = input.ModelFlag ?? EmlangImport.PascalName(Path.GetFileName(input.FilePath).Split('.')[0]);

        IReadOnlyList<AggregateOverride> overrides;
        try
        {
            overrides = input.AggregateFlag.Select(AggregateOverride.Parse)
                .Concat(input.StartsStreamFlag.Select(AggregateOverride.ParseStartsStream))
                .ToList();
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }

        EmlangImportResult result;
        try
        {
            result = EmlangImport.ToCurated(board, model, input.NamespaceFlag, overrides, input.ChapterFlag);
        }
        catch (ArgumentException e) when (e.ParamName == "chapters")
        {
            Console.Error.WriteLine(e.Message.Replace($" (Parameter '{e.ParamName}')", ""));
            return null;
        }

        // A slice the board moved to a chapter left out is still on the board, so a run over some chapters
        // compares the definitions against the whole board's slice names (slice-removed-on-board)
        var boardSlices = input.ChapterFlag.Count == 0
            ? null
            : EmlangImport.ToCurated(board, model, input.NamespaceFlag).Model.Slices.Select(ImportMerge.SliceName).ToList();

        // The spec writer reads the chapters imported, never the ones left out
        board = result.Board ?? board;
        run.Callouts = result.Callouts;
        run.Report.AddRange(result.Report);
        foreach (var line in result.Report) run.Out.WriteLine(line);

        var ns = input.NamespaceFlag ?? CSharpModelWriter.Identifiers.Sanitize(model);
        var specsNamespace = input.SpecsNamespaceFlag ?? ns + ".Specs";

        // One decision, handed to both writers (bobcat#512): the specifications declare identities as the typed
        // values the stubs take, so they cannot be written against two different answers
        var identities = input.StrongTypedIdsFlag
            ? StrongTypedIds.For(result.Model, CSharpModelWriter.StubNames(result.Model))
            : StrongTypedIds.None;

        var specs = EmlangSpecWriter.Write(board, result.Model, ns, specsNamespace, input.StoreFlag, identities);
        var generated = CSharpModelWriter.Write(result.Model, ns, specs.Additions, perChapter: !input.SingleDefinitionFlag, identities);

        if (identities.Callouts.Count > 0)
        {
            run.Callouts = run.Callouts.Concat(identities.Callouts).ToList();
            run.Warn(identities.Callouts);
        }

        var outDirectory = Path.GetFullPath(input.OutFlag ?? Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!);
        Directory.CreateDirectory(outDirectory);

        // Nothing is overwritten without --force: an existing file is merged into instead, so the stubs gain only
        // what the specifications need and a re-run is additive
        var files = new ImportFileWriter(input.ForceFlag);
        run.Files = files.Files;

        var stubs = files.WriteStubs(outDirectory, generated.StubFiles);
        run.Out.WriteLine(
            $"Stubs under {Path.Combine(outDirectory, ModelLayout.FeaturesFolder)}: wrote {stubs.Written}"
            + (stubs.Merged > 0 ? $", merged the members the specifications need into {stubs.Merged}" : "")
            + (stubs.Kept > 0 ? $", kept {stubs.Kept} that already have everything" : "") + ".");

        var definitionFiles = generated.DefinitionFiles.Count > 0
            ? generated.DefinitionFiles
            : [new GeneratedFile($"{model}.cs", generated.Definition)];
        var definitions = files.WriteDefinitions(outDirectory, definitionFiles, result.Model, boardSlices);
        if (definitions.Written + definitions.Merged > 0)
        {
            run.Out.WriteLine(
                $"Wrote {definitions.Written} event model definition(s)"
                + (definitions.Merged > 0 ? $", merged new slices or roles into {definitions.Merged}" : "")
                + ". Register them with AddEventModel<T>() (or let Wolverine discover them).");
        }

        var specsDirectory = Path.GetFullPath(input.SpecsOutFlag
                                              ?? Path.Combine(Path.GetDirectoryName(outDirectory) ?? outDirectory, specsNamespace));
        writeSpecs(result.Model, specs, specsDirectory, files, input.ForceFlag, run);

        foreach (var line in files.Lines)
        {
            run.Out.WriteLine(line);
            run.Report.Add(line);
        }

        run.Warn(files.Callouts.Where(x => !x.Code.StartsWith("merged-", StringComparison.Ordinal)));
        run.Callouts = run.Callouts.Concat(files.Callouts).ToList();

        writeSpecProject(input, model, specsNamespace, outDirectory, specsDirectory, run);

        return result.Model.Model;
    }

    private static void writeSpecs(ImportedEventModel model, GeneratedSpecs specs, string directory, ImportFileWriter files, bool force,
        SpecRun run)
    {
        run.Report.AddRange(specs.Report);
        foreach (var line in specs.Report) run.Out.WriteLine(line);

        Directory.CreateDirectory(directory);
        var counts = new SpecFileWriter(files, force).WriteSpecs(directory, specs.Files);
        run.Out.WriteLine(
            $"Wrote {counts.Written} specification file(s) to {directory} ({specs.Specs} specification(s) in {specs.Features} feature(s) in all). "
            + $"{EmlangSpecWriter.TestSupportFile} starts the application's own host through Alba.");
        if (counts.Kept + counts.Merged == 0) return;

        // What the model has that the specifications there still don't, after the merge: an example whose
        // specification could not be placed (each such file is a callout too)
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        var missing = EmlangSpecWriter.MissingSpecs(model, sources);

        if (counts.Merged > 0) run.Out.WriteLine($"Merged new specifications into {counts.Merged} file(s) that already exist.");
        if (counts.Kept > 0) run.Out.WriteLine($"Kept {counts.Kept} specification file(s) that already exist (--force overwrites them).");
        run.Out.WriteLine(missing.Count == 0
            ? "Every example in the model has a specification."
            : $"{missing.Count} example(s) in the model have no specification:");
        foreach (var name in missing) run.Out.WriteLine($"  {name}");
    }

    /// <summary>
    /// The spec project around the specifications, when the directory has none, and the checks on the application.
    /// Every decision is <see cref="SpecProjectWriter.Plan"/>'s; nothing it names that already exists is written,
    /// with or without --force.
    /// </summary>
    private static void writeSpecProject(WriteSpecsInput input, string model, string specsNamespace, string outDirectory,
        string specsDirectory, SpecRun run)
    {
        var appProject = input.AppProjectFlag is { Length: > 0 } flag
            ? Path.GetFullPath(flag)
            : SpecProjectWriter.FindAppProject(outDirectory);

        var plan = SpecProjectWriter.Plan(new SpecProjectRequest(specsDirectory, specsNamespace, model, appProject,
            input.BobcatVersionFlag, input.WolverineVersionFlag));

        foreach (var file in plan.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.WriteAllText(file.Path, file.Content); // UTF-8 without a BOM
            run.Files.Add(new ImportedFile(file.Path, "write"));
        }

        foreach (var file in plan.Kept) run.Files.Add(new ImportedFile(file.Path, "kept"));

        run.Report.AddRange(plan.Report);
        foreach (var line in plan.Report) run.Out.WriteLine(line);

        run.Warn(plan.Callouts);
        run.Callouts = run.Callouts.Concat(plan.Callouts).ToList();
    }
}
