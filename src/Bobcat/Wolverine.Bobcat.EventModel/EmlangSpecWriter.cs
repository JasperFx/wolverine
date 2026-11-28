using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using JasperFx.CodeGeneration;

using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Scenarios;
using Wolverine.Configuration.EventModeling.Scaffolding;

namespace Wolverine.Bobcat.EventModel;

/// <summary>What <see cref="EmlangSpecWriter.Write"/> produced.</summary>
/// <param name="Files">
/// The specification files, each relative to the spec project (bobcat#440): <c>TestSupport.cs</c>
/// with the fixture, the collection and the base spec classes, then one file per slice under a folder
/// per chapter.
/// </param>
/// <param name="Features">How many feature classes it holds: one per slice with an example.</param>
/// <param name="Specs">How many specifications: one per test with something to say.</param>
/// <param name="Additions">What the stubs need beyond the model's own for the specs to compile.</param>
/// <param name="Report">Every guess and gap, one line each, for the person to go and fix.</param>
public sealed record GeneratedSpecs(
    IReadOnlyList<GeneratedFile> Files,
    int Features,
    int Specs,
    CSharpModelWriter.StubAdditions Additions,
    IReadOnlyList<string> Report);

/// <summary>
/// How <c>write-specs</c> writes the act of a command example (<c>--acts</c>, GH-6004). A board rarely says whether
/// a command arrives over HTTP or as a message, and the specification and the scaffolded skeleton have to agree.
/// </summary>
public enum ActStyle
{
    /// <summary>
    /// The default: the board's <c>apiEndpoint</c> when the command has one; else the trigger decides. A command a
    /// screen triggers is an HTTP endpoint, at the route <c>scaffold</c> writes, and the specification says which
    /// board field would have settled it; a command an automation triggers, or nothing on the board triggers, is a
    /// message.
    /// </summary>
    Board,

    /// <summary>Every command is an HTTP endpoint: the board's <c>apiEndpoint</c>, else the route <c>scaffold</c> writes.</summary>
    Http,

    /// <summary>Every command is a message received, whatever the board says: what <c>write-specs</c> wrote before 7.0.</summary>
    Message,
}

/// <summary>
/// Writes a model's examples as WolverineFx.Bobcat specifications (bobcat#423): one
/// <c>[BobcatFeature]</c> class per slice, one <c>[Fact]</c> per example, each value the example
/// gives as a partial object naming exactly those members.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the scenario model</b> (<see cref="CuratedScenario.Model"/>, bobcat#846; GH-6004), not the
/// element kinds an example names: the act's kind picks <c>WhenReceived</c>, <c>WhenPublished</c>,
/// <c>WhenPosted</c>, <c>WhenReading</c>, <c>WhenTimePasses</c> or <c>WhenScheduledMessagesAreExecuted</c>,
/// and each outcome's kind picks the assertion -- events (tagged, with generated members left out), a read
/// model as one document, its rows or none, a refusal by its kind, a caused message, a consistency boundary.
/// A kind the writer has no words for is reported, never guessed.
/// </para>
/// <para>
/// <b>A specification's identity is the one the model links (bobcat#435).</b> The feature is the
/// SLICE the import attached the test to, not the board chapter it was written under, and the
/// scenario is <see cref="ScenarioTitle"/>, the method name read back as a sentence, which is what
/// a projected test reports. <c>EmlangImport</c> names each scenario the same way, so the
/// definition's <c>LinksToSpecification</c>, the pushed descriptor and a run's
/// <c>scenario_finished</c> all carry one string.
/// </para>
/// <para>
/// <b>The code targets WolverineFx.Bobcat's <c>WolverineSpec</c></b>, and WolverineFx.Bobcat.Http's
/// <c>WolverineHttpSpec</c> for a slice whose act is an HTTP request or a query. This package references
/// neither: it is text, and the project it lands in references both.
/// </para>
/// <para>
/// <b>Types come from the stubs.</b> Every member's type is what
/// <see cref="CSharpModelWriter.FieldsOf"/> gives the stub, so the generated specs and the stubs
/// written beside them always agree. An identity the model does not type is a Guid, declared as a
/// local (<c>var theOrder = Guid.CreateVersion7();</c>) so the rendered spec names it <c>theOrder</c>.
/// An identity the model never names is minted the same way, and the act addresses it through the
/// command's conventional <c>Id</c> (bobcat#438).
/// </para>
/// <para>
/// <b>It is additive.</b> The command writes the file only when it does not exist; run again, it adds a
/// specification for each new example to the existing class (<see cref="SpecMerge.MergeSpecs"/>), and
/// <see cref="MissingSpecs"/> reports the model's tests that still have none.
/// </para>
/// </remarks>
public static class EmlangSpecWriter
{
    /// <summary>The file holding the fixture, the collection and the base specification classes.</summary>
    public const string TestSupportFile = "TestSupport.cs";

    /// <summary>The spec project's global usings, so each specification file holds only its specification.</summary>
    public const string GlobalUsingsFile = "GlobalUsings.cs";

    /// <param name="board">
    /// The board the model was imported from, whose steps declare streams and identities and whose tests the
    /// import may have attached to no slice; null for a model a reader lowered itself (a dcb.events Playground
    /// model, ESDM documents), where everything is on the model.
    /// </param>
    /// <param name="model">The imported model, each scenario carrying its <see cref="CuratedScenario.Model"/>.</param>
    /// <param name="ns">The application's root namespace, where the stubs and the definition live.</param>
    /// <param name="specsNamespace">
    /// The spec project's root namespace, which should be the spec project's name; each chapter
    /// folder adds a segment. Defaults to <c>{ns}.Specs</c>.
    /// </param>
    /// <param name="store">
    /// The application's event store: <c>marten</c>, <c>polecat</c> or <c>fisher</c>. It decides how the
    /// generated host runs the store's async daemon; when null the host names each store's way in a comment.
    /// </param>
    /// <param name="identities">
    /// The strong-typed identifiers the stubs are written with (bobcat#512/#433), so a specification
    /// declares an identity as the typed value -- <c>var theOrder = new OrderId(Guid.CreateVersion7());</c> --
    /// and every member that takes it compiles. <see cref="StrongTypedIds.None"/>, or null, keeps the
    /// Guid it always was.
    /// </param>
    /// <param name="acts">How a command example's act is written: see <see cref="ActStyle"/>.</param>
    public static GeneratedSpecs Write(EmlangBoard? board, ImportedEventModel model, string ns, string? specsNamespace,
        string? store, StrongTypedIds? identities, ActStyle acts)
        => write(board, model, ns, specsNamespace, store, identities ?? StrongTypedIds.None, acts);

    /// <inheritdoc cref="Write(EmlangBoard?, ImportedEventModel, string, string?, string?, StrongTypedIds?, ActStyle)"/>
    public static GeneratedSpecs Write(EmlangBoard board, ImportedEventModel model, string ns, string? specsNamespace,
        string? store, StrongTypedIds? identities)
        => write(board, model, ns, specsNamespace, store, identities ?? StrongTypedIds.None, ActStyle.Board);

    /// <inheritdoc cref="Write(EmlangBoard?, ImportedEventModel, string, string?, string?, StrongTypedIds?, ActStyle)"/>
    public static GeneratedSpecs Write(EmlangBoard board, ImportedEventModel model, string ns, string? specsNamespace = null,
        string? store = null)
        => write(board, model, ns, specsNamespace, store, StrongTypedIds.None, ActStyle.Board);

    /// <summary>The <c>--acts</c> flag's value: <c>board</c> (the default when null or empty), <c>http</c> or <c>message</c>.</summary>
    /// <exception cref="ArgumentException">Anything else.</exception>
    public static ActStyle ParseActs(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "board" => ActStyle.Board,
        "http" => ActStyle.Http,
        "message" or "messages" => ActStyle.Message,
        var other => throw new ArgumentException($"--acts takes board, http or message, not '{other}'.", nameof(text))
    };

    /// <summary>The scenario model an imported scenario carries: bound by the import, else the reader's unbound one.</summary>
    internal static ScenarioModel? ModelOf(CuratedScenario scenario) => scenario.Model ?? scenario.Source?.Model;

    private static GeneratedSpecs write(EmlangBoard? board, ImportedEventModel model, string ns, string? specsNamespace,
        string? store, StrongTypedIds identities, ActStyle acts)
    {
        var context = new Context(model, identities, acts);
        context.Declare(model);
        if (board is not null) context.Declare(board);
        var modelName = CSharpModelWriter.Identifiers.Sanitize(model.Model);
        if (modelName.Length == 0) modelName = "Imported";
        var root = specsNamespace is { Length: > 0 } ? specsNamespace : ns + ".Specs";

        // Bodies first: what they name decides the stubs, and so the namespaces to bring in
        var bodies = new List<(string? Chapter, string FileName, string Body)>();
        var features = 0;
        var specs = 0;
        var classNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slice in model.Slices)
        {
            var scenarios = slice.Specifications?.Scenarios.Where(x => ModelOf(x) is not null).ToList() ?? [];
            if (scenarios.Count == 0)
            {
                context.Report.Add($"slice '{slice.Name}': no tests, so no specifications.");
                continue;
            }

            // A slice and one of its examples often share a name (a view slice and the test of that
            // view), and C# refuses a member named for its enclosing type (CS0542). The class name is
            // not part of the identity, [BobcatFeature] is, so the class gives way, never the method.
            var methodNames = scenarios.Select(x => MethodName(ModelOf(x)!.Name)).ToHashSet(StringComparer.Ordinal);
            var classCandidate = FeatureClassName(slice.Name);
            if (methodNames.Contains(classCandidate)) classCandidate += "_feature";
            var className = unique(classNames, classCandidate);
            features++;

            // The tests first: whether any act is an HTTP request decides the class's base
            var tests = scenarios.Select(x => new TestWriter(context, slice, ModelOf(x)!, x.Source).Write()).ToList();
            var http = tests.Any(x => x.Http);

            using var writer = new SourceWriter();
            foreach (var line in descriptionLines(slice.Description)) writer.WriteLine(line);

            if (slice.Pattern is "Automation" or "Translation" && slice.Trigger?.Label is { Length: > 0 } trigger)
            {
                var said = $"// {slice.Name} is {article(slice.Pattern!)}, triggered by \"{comment(trigger)}\"";
                if (said.Length <= MaxLineLength)
                {
                    writer.WriteLine(said);
                }
                else
                {
                    writer.WriteLine($"// {slice.Name} is {article(slice.Pattern!)}, triggered by");
                    writer.WriteLine($"// \"{comment(trigger)}\"");
                }
            }

            writer.WriteLine($"[BobcatFeature({quote(slice.Name)})]");
            writer.WriteLine(sliceBinding(slice, context));
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}{(http ? "HttpSpec" : "Spec")}(app)");

            var first = true;
            foreach (var test in tests)
            {
                if (!first) writer.BlankLine();
                first = false;

                // The import already deduplicated the slice's scenarios by title, and a title
                // round-trips to exactly one method name, so no method here needs a suffix
                writeTest(writer, test);
                specs++;
            }

            writer.FinishBlock();

            var fileName = CSharpModelWriter.Identifiers.Sanitize(slice.Name);
            bodies.Add((ModelLayout.ChapterFolder(slice.Chapter), fileName.Length == 0 ? className : fileName, writer.Code()));
        }

        // A test the import attached to no slice still gets its specification, under its chapter as
        // before: what it arranges and asserts is still worth writing, and its examples are still
        // fields the stubs need. It binds to nothing on the model, and the report says so.
        if (board is not null)
        {
            var attached = model.Slices
                .SelectMany(x => x.Specifications?.Scenarios ?? [])
                .Select(x => x.Source)
                .OfType<EmlangTest>()
                .ToHashSet(ReferenceEqualityComparer.Instance);

            foreach (var chapter in board.Chapters)
            {
                var orphans = chapter.Tests.Where(x => !attached.Contains(x)).ToList();
                if (orphans.Count == 0) continue;

                var className = unique(classNames, FeatureClassName(chapter.Name));
                features++;

                var tests = orphans.Select(x => new TestWriter(context, null, x.Model, x).Write()).ToList();
                var http = tests.Any(x => x.Http);

                using var writer = new SourceWriter();
                writer.WriteLine("// The model attaches these to no slice, so they bind to nothing on the event model");
                writer.WriteLine($"[BobcatFeature({quote(chapter.Name)})]");
                writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}{(http ? "HttpSpec" : "Spec")}(app)");

                var methods = new HashSet<string>(StringComparer.Ordinal);
                var first = true;
                foreach (var test in tests)
                {
                    if (!first) writer.BlankLine();
                    first = false;

                    context.Report.Add($"⚠ chapter '{chapter.Name}', test '{test.Name}': attached to no slice, so its specification binds to nothing on the model.");
                    writeTest(writer, test with { MethodName = unique(methods, test.MethodName) });
                    specs++;
                }

                writer.FinishBlock();
                var folder = ModelLayout.ChapterFolder(chapter.Name);
                bodies.Add((folder, (folder ?? "Model") + "Unattached", writer.Code()));
            }
        }

        var additions = new CSharpModelWriter.StubAdditions(context.Streams.ToList(), context.Documents.ToList(), context.Elements.ToList(),
            context.ExtraFields.ToDictionary(x => x.Key, x => (IReadOnlyList<CSharpModelWriter.StubField>)x.Value));

        // Every namespace the stubs land in: a spec names types from any chapter, and an unused
        // using costs nothing where a missing one does not compile
        var appNamespaces = CSharpModelWriter.Layout(model, ns, additions).Namespaces;

        var files = new List<GeneratedFile>
        {
            new(GlobalUsingsFile, writeGlobalUsings(appNamespaces, context)),
            new(TestSupportFile, writeTestSupport(root, modelName, store))
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GlobalUsingsFile, TestSupportFile };
        foreach (var (chapter, fileName, body) in bodies)
        {
            var path = uniquePath(paths, chapter is null ? fileName : $"{chapter}/{fileName}");
            files.Add(new GeneratedFile(path, writeSpecFile(ModelLayout.NamespaceFor(root, chapter), body)));
        }

        return new GeneratedSpecs(files, features, specs, additions, context.Report);
    }

    private static string article(string pattern) => pattern == "Automation" ? "an automation" : "a translation";

    /// <summary>One written test: its method, its attribute, and whether its act needs the HTTP base class.</summary>
    private sealed record WrittenTest(string Name, string MethodName, ScenarioStatus Status, IReadOnlyList<string> Lines, bool Http);

    private static void writeTest(SourceWriter writer, WrittenTest test)
    {
        switch (test.Status)
        {
            case ScenarioStatus.Pending:
                writer.WriteLine("// Blocked on the board, so the specification is pending until the slice is ready");
                writer.WriteLine("[BobcatSpec(Pending = true)]");
                break;
            case ScenarioStatus.Informational:
                writer.WriteLine("// Informational on the board: it documents the slice rather than specifying it, so it is pending");
                writer.WriteLine("[BobcatSpec(Pending = true)]");
                break;
            default:
                writer.WriteLine("[Fact]");
                break;
        }

        writer.Write($"BLOCK:public async Task {test.MethodName}()");
        foreach (var line in test.Lines)
        {
            if (line.Length == 0) writer.BlankLine();
            else writer.WriteLine(line);
        }

        writer.FinishBlock();
    }

    /// <summary>A slice's board description as comment lines above its feature class, one per line of the description.</summary>
    private static IEnumerable<string> descriptionLines(string? description)
    {
        if (description is not { Length: > 0 }) yield break;
        foreach (var line in description.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()))
        {
            yield return line.Length == 0 ? "//" : $"// {comment(line)}";
        }
    }

    /// <summary>
    /// bobcat#449: the spec's slice, by the type that bears its name where one does -- the command, or
    /// a view's read model -- so the IDE navigates from the spec to it and a rename keeps the two
    /// together; by name otherwise. <c>SliceType</c> means exactly <c>SliceName = type.Name</c> (#324).
    /// </summary>
    private static string sliceBinding(CuratedSlice slice, Context context)
    {
        var name = CSharpModelWriter.Identifiers.Sanitize(slice.Name);
        var named = new[] { slice.Command }.Concat(slice.ReadModels)
            .OfType<string>()
            .Select(CSharpModelWriter.Identifiers.Sanitize)
            .FirstOrDefault(x => x.Length > 0 && x == name);

        if (named is not null)
        {
            if (!context.Elements.Contains(named)) context.Elements.Add(named);
            return $"[BobcatSlice(SliceType = typeof({named}))]";
        }

        return $"[BobcatSlice(SliceName = {quote(slice.Name)})]";
    }

    private static string uniquePath(HashSet<string> taken, string stem)
    {
        var candidate = stem + ".cs";
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{stem}{i}.cs";
        return candidate;
    }

    // The usings live in GlobalUsings.cs, so a specification file is just its specifications
    private static string writeSpecFile(string ns, string body)
    {
        var builder = new StringBuilder();
        builder.Append("// Generated by `write-specs` (WolverineFx.Bobcat.EventModel) from the model's examples. Nothing regenerates it.\n");
        builder.Append('\n');
        builder.Append($"namespace {ns};\n");
        builder.Append('\n');
        builder.Append(body.TrimEnd('\n', '\r'));
        builder.Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// The model's scenarios with no specification among <paramref name="sources"/>, as
    /// <c>slice / scenario</c>: what a second run reports instead of writing over the first.
    /// </summary>
    public static IReadOnlyList<string> MissingSpecs(ImportedEventModel model, IEnumerable<string> sources)
    {
        var written = new HashSet<(string Feature, string Method)>();
        foreach (var source in sources)
        {
            string? feature = null;
            foreach (var line in source.Split('\n'))
            {
                if (FeatureAttribute.Match(line) is { Success: true } attribute)
                {
                    feature = Regex.Unescape(attribute.Groups[1].Value);
                }
                else if (feature is not null && TestMethod.Match(line) is { Success: true } method)
                {
                    written.Add((feature, method.Groups[1].Value));
                }
            }
        }

        return model.Slices
            .SelectMany(slice => (slice.Specifications?.Scenarios ?? [])
                .Where(x => ModelOf(x) is { } scenario && !written.Contains((slice.Name, MethodName(scenario.Name))))
                .Select(x => $"{slice.Name} / {x.Name}"))
            .ToList();
    }

    private static readonly Regex FeatureAttribute = new(@"\[BobcatFeature\(""((?:[^""\\]|\\.)*)""\)\]", RegexOptions.Compiled);
    private static readonly Regex TestMethod = new(@"public\s+async\s+Task\s+(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>A slice's class name: snake case, as Bobcat feature classes are written.</summary>
    public static string FeatureClassName(string slice)
    {
        var name = snake(slice);

        // A single lowercase word is reserved for future keywords (CS8981)
        return name.Contains('_') ? name : name + "_slice";
    }

    /// <summary>A test's method name: snake case.</summary>
    public static string MethodName(string test) => snake(test);

    /// <summary>
    /// The scenario title a test's generated specification reports: its <see cref="MethodName"/>
    /// read back as a sentence, the way the projected lane titles every test (bobcat#435).
    /// <c>EmlangImport</c> names the model's scenario this, so the two always agree.
    /// </summary>
    public static string ScenarioTitle(string test) => ProjectedSpecNaming.ScenarioTitleFor(MethodName(test));

    /// <summary>
    /// <c>GlobalUsings.cs</c>: every namespace a specification names, once for the whole project, and
    /// what the generated specifications are.
    /// </summary>
    private static string writeGlobalUsings(IReadOnlyList<string> appNamespaces, Context context)
    {
        var builder = new StringBuilder();
        builder.Append("// Generated by `write-specs` (WolverineFx.Bobcat.EventModel). Nothing regenerates this file.\n");
        builder.Append("//\n");
        builder.Append("// The specifications hold one test per example in the model, each value a partial object that\n");
        builder.Append("// names only what the example names. Run the import again and it adds a specification for each\n");
        builder.Append("// new example, without touching the ones here.\n");
        builder.Append('\n');

        var namespaces = new List<string> { "System", "System.Threading.Tasks", "Bobcat", "Bobcat.Xunit", "Wolverine.Bobcat", "Wolverine.Bobcat.Http", "Xunit" };
        if (context.UsesProblemDetails) namespaces.Add("Microsoft.AspNetCore.Mvc");
        if (context.UsesEventTagQuery) namespaces.Add("JasperFx.Events.Tags");

        foreach (var ns in namespaces.Concat(appNamespaces))
        {
            builder.Append($"global using {ns};\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>TestSupport.cs</c> (bobcat#440): the fixture, the collection and the base specification
    /// classes, kept out of the specification files so the host is configured in one file that holds
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// The fixture is the application's own host, started through Alba as it is configured in
    /// production, with Wolverine in solo mode and external transports off. Nothing in the
    /// specifications depends on the store: <c>WolverineSpec</c> works against the application's
    /// <c>IEventStore</c>, and <c>ResetAsync</c> resets every event store the host registers. Only how
    /// the store's async daemon runs differs, which <paramref name="store"/> decides. A slice whose act is
    /// an HTTP request or a query gets the second base class, over <c>WolverineHttpSpec</c> (GH-6004).
    /// </remarks>
    private static string writeTestSupport(string ns, string modelName, string? store)
    {
        var daemon = store?.Trim().ToLowerInvariant() switch
        {
            "marten" => new[] { "// Marten's async daemon, in solo mode so projections run in this process", "services.MartenDaemonModeIsSolo();" },
            "polecat" => ["// Polecat's async daemon runs in solo mode where the application calls AddAsyncDaemon(DaemonMode.Solo)"],
            "fisher" => ["// Fisher's AddAsyncDaemon() already runs in solo mode"],
            null or "" =>
            [
                "// For async projections, run the store's daemon in solo mode, in this process:",
                "//   Marten:  services.MartenDaemonModeIsSolo();",
                "//   Polecat: AddAsyncDaemon(DaemonMode.Solo) where the application configures Polecat",
                "//   Fisher:  AddAsyncDaemon() already runs in solo mode"
            ],
            var other => throw new ArgumentOutOfRangeException(nameof(store), other, "The store is marten, polecat or fisher")
        };

        using var writer = new SourceWriter();
        writer.WriteLine("// Generated by `write-specs` (WolverineFx.Bobcat.EventModel). Nothing regenerates this file.");
        writer.BlankLine();
        writer.WriteLine("using Alba;");
        if (store?.Trim().ToLowerInvariant() == "marten") writer.WriteLine("using Marten;");
        writer.WriteLine("using Wolverine;");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// The application under test: its own host, configured as it is in production and started once for");
        writer.WriteLine("/// every specification. Alba needs the application's Program to be public, so the application");
        writer.WriteLine("/// declares <c>public partial class Program;</c>.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine("/// <remarks>");
        writer.WriteLine("/// The specifications use the application's own database and schema, so the data a failing");
        writer.WriteLine("/// specification leaves behind is where a developer would look for it.");
        writer.WriteLine("/// </remarks>");
        writer.Write("BLOCK:public class AppFixture : IAsyncLifetime");
        writer.WriteLine("public IAlbaHost Host { get; private set; } = null!;");
        writer.BlankLine();
        writer.WriteLine("public async ValueTask InitializeAsync()");
        writer.WriteLine("    => Host = await AlbaHost.For<Program>(x => x.ConfigureServices(services =>");
        writer.WriteLine("    {");
        writer.WriteLine("        services.DisableAllExternalWolverineTransports();");
        writer.WriteLine("        services.RunWolverineInSoloMode();");
        writer.BlankLine();
        foreach (var line in daemon) writer.WriteLine("        " + line);
        writer.BlankLine();
        writer.WriteLine("        // Under xUnit v3 the test project is the entry assembly, which Wolverine would otherwise");
        writer.WriteLine("        // adopt as the application assembly, and then find no handlers or endpoints in");
        writer.WriteLine("        services.ConfigureWolverine(opts => opts.ApplicationAssembly = typeof(Program).Assembly);");
        writer.WriteLine("    }));");
        writer.BlankLine();
        writer.WriteLine("public async ValueTask DisposeAsync() => await Host.DisposeAsync();");
        writer.FinishBlock();
        writer.BlankLine();
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Every specification shares the one host and runs one at a time, because each resets the event store.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine($"[CollectionDefinition({quote(modelName)})]");
        writer.WriteLine($"public class {modelName}Collection : ICollectionFixture<AppFixture>;");
        writer.BlankLine();
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// The base of every specification: the Given/When/Then vocabulary, and a reset before each test.");
        writer.WriteLine("/// ResetAsync resets every event store the host registers (Marten, Polecat or Fisher), so each");
        writer.WriteLine("/// specification starts from an empty store. Nothing is torn down afterwards, on purpose.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine($"[Collection({quote(modelName)})]");
        writer.Write($"BLOCK:public abstract class {modelName}Spec(AppFixture app) : WolverineSpec(app.Host), IAsyncLifetime");
        writer.WriteLine("public async ValueTask InitializeAsync() => await ResetAsync();");
        writer.BlankLine();
        writer.WriteLine("public ValueTask DisposeAsync() => ValueTask.CompletedTask;");
        writer.FinishBlock();
        writer.BlankLine();
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// The base of a specification whose act is an HTTP request or a query: the same host through Alba, with");
        writer.WriteLine("/// WhenPosted, WhenReading and the response assertions of WolverineFx.Bobcat.Http, and the same reset.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine($"[Collection({quote(modelName)})]");
        writer.Write($"BLOCK:public abstract class {modelName}HttpSpec(AppFixture app) : WolverineHttpSpec(app.Host), IAsyncLifetime");
        writer.WriteLine("public async ValueTask InitializeAsync() => await ResetAsync();");
        writer.BlankLine();
        writer.WriteLine("public ValueTask DisposeAsync() => ValueTask.CompletedTask;");
        writer.FinishBlock();
        return writer.Code();
    }

    private sealed class Context(ImportedEventModel model, StrongTypedIds identities, ActStyle acts)
    {
        /// <summary>The strong-typed identifiers the stubs use, so a spec's locals take the same types.</summary>
        public StrongTypedIds Ids { get; } = identities;

        /// <summary>How a command example's act is written (<c>--acts</c>).</summary>
        public ActStyle Acts { get; } = acts;

        /// <summary>A specification named <c>ProblemDetails</c>, so the usings bring in Microsoft.AspNetCore.Mvc.</summary>
        public bool UsesProblemDetails { get; set; }

        /// <summary>A specification named <c>EventTagQuery</c>, so the usings bring in JasperFx.Events.Tags.</summary>
        public bool UsesEventTagQuery { get; set; }

        /// <summary>
        /// The primitive a member's type IS: a strong-typed identity's inner <c>Guid</c> or
        /// <c>string</c>, else the type itself. Every decision about how to render or declare a value
        /// asks this, so <c>OrderId</c> takes exactly the path <c>Guid</c> takes.
        /// </summary>
        public string Primitive(string type) => Ids.Inner(type) ?? type;

        private readonly Dictionary<string, (string? Stream, List<string> Identities, List<string> Props)> _declared = new(StringComparer.Ordinal);
        private readonly HashSet<string> _events = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether a type is an event the model knows: a slice emits or consumes it, the import put it on a stream,
        /// or a board step declares it. An automation's trigger is rewritten as the act only when it is one; an EM
        /// Spec automation is triggered by its processor, which is no event to receive.
        /// </summary>
        public bool IsEvent(string typeName) => _events.Contains(typeName) || Model.EventStreams.ContainsKey(typeName);

        /// <summary>
        /// What the model's own steps say about each element: its stream, and which props are its
        /// identity. A scenario item often repeats neither: an eventmodelers.ai export names the
        /// aggregate on the element, not on every example of it (bobcat#433 discussion, case C).
        /// </summary>
        public void Declare(EmlangBoard board)
        {
            foreach (var step in board.Chapters.SelectMany(x => x.Steps))
            {
                if (step.Kind is not (EmlangElementKind.Command or EmlangElementKind.Event or EmlangElementKind.View)) continue;

                // bobcat#439: only a stream the model DECLARES. An emlang swimlane names who acts
                // (Admin / Volunteer approved), not where the event is stored, and reading it as a
                // stream spread one application's events over Admin, Volunteer and ShelterStaff.
                var stream = step.Stream is { Length: > 0 } declared ? declared : null;
                declare(TypeName(step.Label), stream, step.Identities, step.Props.Keys);
                if (step.Kind == EmlangElementKind.Event) _events.Add(TypeName(step.Label));
            }
        }

        /// <summary>
        /// The same, from the imported model's element hints (GH-6004): what a reader that lowers a model
        /// itself, with no board behind it, says about each element's identities and fields.
        /// </summary>
        public void Declare(ImportedEventModel imported)
        {
            foreach (var slice in imported.Slices)
            {
                foreach (var (name, element) in slice.Elements)
                {
                    var typeName = CSharpModelWriter.Identifiers.Sanitize(name);
                    declare(typeName, null, element.Identities, element.Fields.Keys.Concat(element.DeclaredFields.Keys));
                }

                foreach (var @event in slice.Events.Concat(slice.ConsumedEvents)) _events.Add(CSharpModelWriter.Identifiers.Sanitize(@event));
            }
        }

        private void declare(string typeName, string? stream, IEnumerable<string> identities, IEnumerable<string> props)
        {
            if (!_declared.TryGetValue(typeName, out var known))
            {
                _declared[typeName] = (stream, identities.Distinct(StringComparer.Ordinal).ToList(), props.Distinct(StringComparer.Ordinal).ToList());
                return;
            }

            if (known.Stream is null && stream is not null) _declared[typeName] = known with { Stream = stream };
            foreach (var identity in identities.Where(x => !known.Identities.Contains(x))) known.Identities.Add(identity);
            foreach (var prop in props.Where(x => !known.Props.Contains(x))) known.Props.Add(prop);
        }

        /// <summary>
        /// The stream an element is stored on: the aggregate the import declared or inferred for an
        /// event (bobcat#444), else what the model's steps declare, else null.
        /// </summary>
        public string? StreamOf(string typeName)
            => Model.EventStreams.GetValueOrDefault(typeName) ?? _declared.GetValueOrDefault(typeName).Stream;

        /// <summary>
        /// The props that are an element's identity: the ones the model marks (<c>idAttribute</c>),
        /// or, when it marks none, the prop named for its stream (<c>orderId</c> on an <c>Order</c>
        /// event) or the generic <c>aggregateId</c> some models use.
        /// </summary>
        public IReadOnlyList<string> IdentitiesOf(string typeName)
        {
            if (!_declared.TryGetValue(typeName, out var known)) return [];
            if (known.Identities.Count > 0) return known.Identities;

            var stream = StreamOf(typeName) is { Length: > 0 } declared ? TypeName(declared) : null;
            return known.Props
                .Where(x => EmlangImport.PascalName(x) is var name
                            && (name == "AggregateId" || (stream is not null && name == stream + "Id")))
                .Take(1)
                .ToList();
        }

        private readonly Dictionary<string, IReadOnlyList<CSharpModelWriter.StubField>> _fields = new(StringComparer.Ordinal);

        public ImportedEventModel Model { get; } = model;
        public List<string> Report { get; } = [];
        public SortedSet<string> Streams { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Documents { get; } = new(StringComparer.Ordinal);

        /// <summary>Every command, event and view a specification names, in the order first named.</summary>
        public List<string> Elements { get; } = [];

        /// <summary>
        /// Fields an example names that the model's hints never reached, such as a test that attaches
        /// to no slice: added to the stub so the specification that names them compiles.
        /// </summary>
        public Dictionary<string, List<CSharpModelWriter.StubField>> ExtraFields { get; } = new(StringComparer.Ordinal);

        /// <summary>The member a prop names on a stub, and its type, exactly as the stub declares it.</summary>
        /// <param name="sample">The example's value, which types a field the stub does not have yet.</param>
        public (string Name, string Type) Member(string typeName, string prop, object? sample = null)
        {
            var name = CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop));
            if (name == typeName) name += "Value";

            if (!_fields.TryGetValue(typeName, out var fields))
            {
                fields = CSharpModelWriter.FieldsOf(Model, typeName, Ids);
                _fields[typeName] = fields;
            }

            if (fields.FirstOrDefault(x => x.Name == name) is { } declared) return (name, declared.Type);

            if (!ExtraFields.TryGetValue(typeName, out var extra)) ExtraFields[typeName] = extra = [];
            if (extra.FirstOrDefault(x => x.Name == name) is { } added) return (name, added.Type);

            var type = name.EndsWith("Id", StringComparison.Ordinal) ? Ids.IdOfMember(typeName, name) ?? "Guid"
                : sample is List<object> ? CuratedFieldTypes.StringList
                : CuratedFieldTypes.TryInfer(EmlangReader.Text(sample), out var inferred) ? inferred
                : "string";
            extra.Add(new CSharpModelWriter.StubField(name, type));
            return (name, type);
        }
    }

    /// <summary>
    /// One element an example names, with the values the example gives it: a given or emitted event, a context
    /// or expected read model (or one of its rows), the act's message or body, a caused message. What the
    /// identity pass and the partial writer work over, whatever kind of given, act or outcome it came from.
    /// </summary>
    private sealed class Ref(EmlangElementKind kind, string label, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, object?> raw)
    {
        public EmlangElementKind Kind => kind;
        public string Label => label;
        public IReadOnlyDictionary<string, string> Values => values;

        /// <summary>The source's raw values where the board kept them (a list), for the one value shape text loses.</summary>
        public IReadOnlyDictionary<string, object?> Raw => raw;

        public string TypeName { get; } = EmlangSpecWriter.TypeName(label);

        /// <summary>The stream the model puts the event on, when the scenario says; null leaves it to the slice's.</summary>
        public string? Stream { get; init; }

        /// <summary>DCB tag name → value, for a tagged event.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = NoValues;

        /// <summary>Members a comparison leaves out: the model's <c>generated</c> fields.</summary>
        public IReadOnlyList<string> Ignored { get; init; } = [];

        /// <summary>
        /// The value that types a member the stubs do not have yet: the raw value where the board kept one (a list),
        /// else the text itself, so an element only the scenario model names still gets <c>int</c> for <c>10</c>.
        /// </summary>
        public object? Sample(string prop) => raw.GetValueOrDefault(prop) ?? (values.TryGetValue(prop, out var text) ? text : null);
    }

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, object?> NoRaw = new Dictionary<string, object?>();

    /// <summary>One test's body. A class rather than a method because a test accumulates state as it goes: its identities.</summary>
    private sealed class TestWriter
    {
        private readonly Context _context;
        private readonly CuratedSlice? _slice;
        private readonly ScenarioModel _model;
        private readonly List<string> _lines = [];

        // An identity's example value ("order-123") → the local that stands for it
        private readonly Dictionary<string, (string Variable, string Type)> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);

        // What the example names, by part
        private readonly List<Ref> _given = [];
        private readonly List<Ref> _contextModels = [];
        private readonly Ref? _command;
        private readonly Ref? _trigger;
        private readonly Ref? _foreign;
        private readonly Ref? _query;
        private readonly List<Ref> _emitted = [];
        private readonly List<(ReadModelOutcome Outcome, Ref Document, List<Ref> Rows)> _views = [];
        private readonly List<Refusal> _refusals = [];
        private readonly List<(MessageCaused Outcome, Ref Message)> _caused = [];
        private readonly BoundaryExpectation? _boundary;
        private readonly bool _nothingEmitted;

        // Where the source kept a value's shape (a list), by the element it was given on
        private readonly Dictionary<(EmlangElementKind, string), IReadOnlyDictionary<string, object?>> _raw = [];

        private bool _http;

        public TestWriter(Context context, CuratedSlice? slice, ScenarioModel model, EmlangTest? source)
        {
            _context = context;
            _slice = slice;
            _model = model;

            foreach (var reference in (source?.Given ?? []).Concat(source?.When ?? []).Concat(source?.Then ?? []))
            {
                _raw.TryAdd((reference.Kind, TypeName(reference.Label)), reference.Values);
            }

            foreach (var given in model.GivenEvents)
            {
                _given.Add(new Ref(EmlangElementKind.Event, given.Event, given.Values, raw(EmlangElementKind.Event, given.Event))
                {
                    Stream = declaredStream(given.Stream),
                    Tags = given.Tags,
                });
            }

            foreach (var decidedAgainst in model.Context)
            {
                if (decidedAgainst.Rows is { Count: > 0 } rows)
                {
                    _contextModels.AddRange(rows.Select(row => new Ref(EmlangElementKind.View, decidedAgainst.ReadModel, row, NoRaw)));
                }
                else
                {
                    _contextModels.Add(new Ref(EmlangElementKind.View, decidedAgainst.ReadModel, decidedAgainst.Values,
                        raw(EmlangElementKind.View, decidedAgainst.ReadModel)));
                }
            }

            switch (model.Act)
            {
                case MessageReceived received:
                    _command = new Ref(EmlangElementKind.Command, received.Message, received.Values, raw(EmlangElementKind.Command, received.Message));
                    break;
                case MessagePublished published:
                    _command = new Ref(EmlangElementKind.Command, published.Message, published.Values, raw(EmlangElementKind.Command, published.Message));
                    break;
                case HttpRequest http:
                    if (http.Body is { Length: > 0 } body)
                    {
                        _command = new Ref(EmlangElementKind.Command, body, http.Values, raw(EmlangElementKind.Command, body));
                    }

                    break;
                case ReadModelQueried queried:
                    _query = new Ref(EmlangElementKind.View, queried.ReadModel, queried.Parameters, NoRaw);
                    break;
                case ForeignEventReceived foreign:
                    _foreign = new Ref(EmlangElementKind.Event, foreign.Event, foreign.Values, raw(EmlangElementKind.Event, foreign.Event));
                    break;
            }

            // An automation's example exercises the command, but what the slice does is take the trigger in and
            // issue the command: the act is the trigger, the command is what it caused (GH-6004)
            if (slice?.Pattern is "Automation" or "Translation" && _command is not null && model.Act is MessageReceived
                && slice.Trigger?.Label is { Length: > 0 } trigger && TypeName(trigger) != _command.TypeName && context.IsEvent(TypeName(trigger)))
            {
                _trigger = new Ref(EmlangElementKind.Event, trigger, NoValues, NoRaw);
            }

            foreach (var outcome in model.Outcomes)
            {
                switch (outcome)
                {
                    case EventEmitted emitted:
                        _emitted.Add(new Ref(EmlangElementKind.Event, emitted.Event, emitted.Values, raw(EmlangElementKind.Event, emitted.Event))
                        {
                            Stream = declaredStream(emitted.Stream),
                            Tags = emitted.Tags,
                            Ignored = emitted.IgnoredMembers,
                        });
                        break;
                    case ReadModelOutcome view:
                        _views.Add((view,
                            new Ref(EmlangElementKind.View, view.ReadModel, view.Values, raw(EmlangElementKind.View, view.ReadModel)),
                            view.Rows.Select(row => new Ref(EmlangElementKind.View, view.ReadModel, row, NoRaw)).ToList()));
                        break;
                    case Refusal refusal:
                        _refusals.Add(refusal);
                        break;
                    case MessageCaused caused:
                        _caused.Add((caused, new Ref(EmlangElementKind.Command, caused.Message, caused.Values, NoRaw)));
                        break;
                    case BoundaryExpectation boundary:
                        _boundary = boundary;
                        break;
                    case NothingEmitted:
                        _nothingEmitted = true;
                        break;
                    default:
                        context.Report.Add($"⚠ {where}: the outcome '{outcome.GetType().Name}' has no specification form yet, so it is left out.");
                        break;
                }
            }
        }

        private IReadOnlyDictionary<string, object?> raw(EmlangElementKind kind, string label)
            => _raw.GetValueOrDefault((kind, TypeName(label))) ?? NoRaw;

        // The EM Spec "default" aggregate is the slice's, which the import already resolved: not a stream of its own
        private static string? declaredStream(string? stream)
            => stream is { Length: > 0 } && !stream.Equals("default", StringComparison.OrdinalIgnoreCase) ? stream : null;

        private string where => $"slice '{_slice?.Name ?? _model.Chapter ?? "(none)"}', test '{_model.Name}'";

        /// <summary>Every element the example names, in the order the specification reads them.</summary>
        private IEnumerable<Ref> references()
        {
            foreach (var reference in _given) yield return reference;
            foreach (var reference in _contextModels) yield return reference;
            if (_trigger is not null) yield return _trigger;
            if (_foreign is not null) yield return _foreign;
            if (_command is not null) yield return _command;
            if (_query is not null) yield return _query;
            foreach (var reference in _emitted) yield return reference;
            foreach (var (_, document, rows) in _views)
            {
                yield return document;
                foreach (var row in rows) yield return row;
            }

            foreach (var (_, message) in _caused) yield return message;
        }

        /// <summary>Whether the example's act is a command: a message, an HTTP request with a body, or a foreign event.</summary>
        private bool actsOnACommand => _command is not null || _foreign is not null;

        public WrittenTest Write()
        {
            var lines = Lines().ToList();
            return new WrittenTest(_model.Name, MethodName(_model.Name), _model.Status, lines, _http);
        }

        private IEnumerable<string> Lines()
        {
            notes();
            declareIdentities();
            if (_ids.Count > 0 || _mintedLocals.Count > 0) _lines.Add("");

            var arranged = given();
            act();
            assert(arranged);

            // A minted identity whose only element wrote no partial is referenced nowhere: drop it
            foreach (var variable in _mintedLocals)
            {
                var declaration = $"var {variable} = ";
                var used = _lines.Any(x => !x.StartsWith(declaration, StringComparison.Ordinal)
                                           && Regex.IsMatch(x, $@"\b{variable}\b"));
                if (!used) _lines.RemoveAll(x => x.StartsWith(declaration, StringComparison.Ordinal));
            }

            if (_lines.Count > 0 && _lines[0].Length == 0) _lines.RemoveAt(0);
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);

            // A partial written as a table spans several lines
            return _lines.SelectMany(x => x.Split('\n'));
        }

        /// <summary>What the board says in prose: the example's context note, and who acts. Comments, never steps.</summary>
        private void notes()
        {
            if (_model.Note is { Length: > 0 } note)
            {
                foreach (var line in note.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    _lines.Add($"// {comment(line)}");
                }
            }

            if (_model.Actor is { Length: > 0 } actor) _lines.Add($"// Acted by {comment(actor)}");
            if (_model.Tenant is { Length: > 0 } tenant)
            {
                _lines.Add($"// Tenant {quote(tenant)}: every store step is the tenant's; the act is sent the way the application takes a tenant");
            }

            if (_lines.Count > 0) _lines.Add("");
        }

        /// <summary>One local per identity the example names: typed ones (Guids) first, then string ids.</summary>
        private void declareIdentities()
        {
            var references = this.references().ToList();

            // A strong-typed identity takes the pass of what it wraps (bobcat#512): OrderId is a Guid
            // identity that happens to say so in the type system, not a third kind of value
            foreach (var wanted in new[] { "Guid", "string" })
            {
                foreach (var reference in references)
                {
                    var typeName = reference.TypeName;
                    foreach (var (prop, value) in reference.Values)
                    {
                        var (member, type) = _context.Member(typeName, prop, reference.Sample(prop));
                        if (_context.Primitive(type) != wanted || value.Length == 0 || _ids.ContainsKey(value)) continue;
                        if (type == "string" && !member.EndsWith("Id", StringComparison.Ordinal)) continue;

                        declare(member, value, type);
                    }
                }
            }

            // An example that names a stream's identity is that stream's local, for every element
            // on the stream that leaves its own identity out
            foreach (var reference in references)
            {
                var typeName = reference.TypeName;
                if (streamName(reference) is not { } stream || _streams.ContainsKey(stream)) continue;

                foreach (var (prop, value) in reference.Values)
                {
                    if (!identityProp(typeName, stream, prop)) continue;
                    if (_ids.TryGetValue(value, out var id)) _streams[stream] = id.Variable;
                }
            }

            // The model marks an identity but the example gives it no value (bobcat#433 discussion,
            // case C): mint one local per stream, and set it on every element that names none
            foreach (var reference in references)
            {
                var typeName = reference.TypeName;
                var stream = streamName(reference);

                foreach (var prop in _context.IdentitiesOf(typeName))
                {
                    if (reference.Values.TryGetValue(prop, out var given) && given.Length > 0) continue;

                    var (member, type) = _context.Member(typeName, prop);
                    var owner = stream ?? (member.EndsWith("Id", StringComparison.Ordinal) && member.Length > 2 ? member[..^2] : member);
                    if (!_streams.TryGetValue(owner, out var variable))
                    {
                        variable = mint(owner, type);
                        _streams[owner] = variable;
                    }

                    if (!_minted.TryGetValue(reference, out var fields)) _minted[reference] = fields = [];
                    if (fields.All(x => x.Member != member)) fields.Add((member, variable));
                }
            }
        }

        // A stream (aggregate) -> the local that stands for its identity in this test
        private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal);

        // An identity the model marks but the example leaves out, set on the element's partial
        private readonly Dictionary<Ref, List<(string Member, string Variable)>> _minted = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The stream (aggregate type) of a reference's element: what the scenario declares for it, else what
        /// the model declares, else what the import inferred from the examples (bobcat#444). Null only when
        /// none names one, and then the events go on a stream with no aggregate type, which Marten, Polecat
        /// and Fisher all allow (bobcat#439). Never the swimlane, which is an actor.
        /// </summary>
        private string? streamName(Ref reference)
            => reference.Stream is { Length: > 0 } own ? TypeName(own)
                : _context.StreamOf(reference.TypeName) is { Length: > 0 } declared ? TypeName(declared) : null;

        private bool identityProp(string typeName, string stream, string prop)
        {
            if (_context.IdentitiesOf(typeName).Contains(prop)) return true;
            if (EmlangImport.PascalName(prop) == "AggregateId") return true;
            return CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop)) == stream + "Id";
        }

        private readonly List<string> _mintedLocals = [];

        /// <summary>Whether a reference has anything to put in a partial: example values, or a minted identity.</summary>
        private bool hasValues(Ref reference) => reference.Values.Count > 0 || _minted.ContainsKey(reference);

        /// <summary>A local for an identity the example never gives a value.</summary>
        private string mint(string owner, string type)
        {
            var name = "the" + owner;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;
            _mintedLocals.Add(variable);

            _lines.Add(_context.Primitive(type) == "Guid"
                ? $"var {variable} = {wrap(type, "Guid.CreateVersion7()")};"
                : $"var {variable} = {wrap(type, quote(EmlangImport.PascalName(owner).ToLowerInvariant() + "-1"))};");
            return variable;
        }

        /// <summary>
        /// An identity value in its declared type: <c>new OrderId(...)</c> for a strong-typed identifier
        /// (bobcat#512), the expression itself for a primitive one.
        /// </summary>
        private string wrap(string type, string expression)
            => _context.Ids.Contains(type) ? $"new {type}({expression})" : expression;

        private void declare(string member, string value, string type)
        {
            var stem = member.EndsWith("Id", StringComparison.Ordinal) && member.Length > 2 ? member[..^2] : member;
            var name = "the" + stem;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;

            _ids[value] = (variable, type);
            _lines.Add(_context.Primitive(type) == "Guid"
                ? $"var {variable} = {wrap(type, "Guid.CreateVersion7()")}; // {quote(value)} in the model"
                : $"var {variable} = {wrap(type, quote(value))};");
        }

        /// <summary>A store step's receiver: the tenant's view when the example names a tenant, else the spec itself.</summary>
        private string store => _model.Tenant is { Length: > 0 } tenant ? $"ForTenant({quote(tenant)})." : "";

        /// <returns>Whether any event was arranged.</returns>
        private bool given()
        {
            if (_model.Clock is { Length: > 0 } clock) clockIs(clock);

            var groups = new List<(string? Stream, string Key, List<PartialSpec> Events)>();
            foreach (var reference in _given)
            {
                var stream = streamOf(reference);
                var key = keyOf(stream, reference);
                var group = groups.FirstOrDefault(x => x.Stream == stream && x.Key == key);
                if (group.Events is null)
                {
                    group = (stream, key, []);
                    groups.Add(group);
                }

                group.Events.Add(partial(reference));
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var (stream, key, events) = groups[i];
                if (i == 0) _actKey = key;
                if (stream is not null) _streamKeys.TryAdd(stream, key);

                // No declared aggregate: a stream with no aggregate type (bobcat#439)
                if (stream is null)
                {
                    call($"await {store}GivenEvents({key}, ", events, ");");
                    continue;
                }

                var verb = i == 0 ? "GivenEvents" : "GivenEventsOn";
                call($"await {store}{verb}<{stream}>({key}, ", events, ");");
            }

            // Stored directly: GivenReadModel bypasses the projection, as the model does
            foreach (var view in _contextModels)
            {
                var typeName = document(view.Label);
                emit($"await {store}GivenReadModel<{typeName}>(", documentPartial(typeName, view), ");");
            }

            if (groups.Count == 0 && _command is not null && _trigger is null)
            {
                // The act runs against a stream that starts empty: name it, from the first event expected
                var expected = _emitted.FirstOrDefault(x => streamName(x) is not null);
                if (expected is not null && streamName(expected) is { } stream
                    && (identityOf(stream, expected) ?? _streams.GetValueOrDefault(stream)) is { } key)
                {
                    _context.Streams.Add(stream);
                    if (!carriesIdentity(stream, key))
                    {
                        // bobcat#453: the handler mints the new stream's id, so no value declared here can
                        // reach it. The id is read back from the act instead, after it runs.
                        _startedKey = (stream, key);
                    }
                    else
                    {
                        _actKey = key;
                        _lines.Add($"await {store}GivenNoEventsFor<{stream}>({key});");
                    }
                }
            }

            if (_lines.Count > 0 && _lines[^1].Length > 0) _lines.Add("");
            return groups.Count > 0;
        }

        /// <summary>The clock the example runs under: frozen at the instant or the date the source wrote, when it is one.</summary>
        private void clockIs(string clock)
        {
            var text = clock.Trim();
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                _lines.Add($"GivenTheDateIs(DateOnly.Parse({quote(text)}));");
            }
            else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                _lines.Add($"GivenTheTimeIs(DateTimeOffset.Parse({quote(text)}));");
            }
            else
            {
                _lines.Add($"// TODO: the model sets the clock to \"{comment(text)}\", which is not a date or an instant");
                _context.Report.Add($"⚠ {where}: the clock '{text}' is not a date or an instant, so the specification leaves it a TODO.");
            }
        }

        private void act()
        {
            if (_trigger is not null)
            {
                // The automation's trigger goes in; the command it issues is checked as caused
                emit(_slice?.Pattern == "Translation" ? "await WhenPublished(" : "await WhenReceived(", partial(_trigger), ");");
                _lines.Add("");
                return;
            }

            switch (_model.Act)
            {
                case null:
                    return;

                case MessageReceived when _command is not null:
                    if (httpFor(_slice)) conventionalHttp();
                    else emit("await WhenReceived(", addressed(_command), ");");
                    break;

                case MessagePublished when _command is not null:
                    emit("await WhenPublished(", addressed(_command), ");");
                    break;

                case HttpRequest http:
                    if (_context.Acts == ActStyle.Message && _command is not null) emit("await WhenReceived(", addressed(_command), ");");
                    else httpRequest(http);
                    break;

                case ReadModelQueried queried:
                    query(queried);
                    break;

                case TimeAdvances advances:
                    timeAdvances(advances.By);
                    break;

                case ForeignEventReceived when _foreign is not null:
                    emit("await WhenPublished(", partial(_foreign), ");");
                    break;

                case ScheduledMessagesExecute:
                    _lines.Add("await WhenScheduledMessagesAreExecuted();");
                    break;

                default:
                    _lines.Add($"// TODO: the model's act '{_model.Act.GetType().Name}' has no specification form yet");
                    _context.Report.Add($"⚠ {where}: the act '{_model.Act.GetType().Name}' has no specification form yet.");
                    break;
            }

            _lines.Add("");
        }

        private void emit(string open, PartialSpec argument, string close) => _lines.Add(rendered(open, argument, close));

        /// <summary>
        /// Whether a command the board gives no <c>apiEndpoint</c> is an HTTP endpoint (<c>--acts</c>): always under
        /// <c>http</c>, never under <c>message</c>, and under <c>board</c> when a screen triggers the slice.
        /// </summary>
        private bool httpFor(CuratedSlice? slice) => _context.Acts switch
        {
            ActStyle.Http => true,
            ActStyle.Message => false,
            _ => slice?.Trigger?.Kind == "Human",
        };

        /// <summary>
        /// The command as an HTTP endpoint the board never named: POSTed at the route <c>scaffold</c> writes for it,
        /// said so, and the board field that would have settled it named.
        /// </summary>
        private void conventionalHttp()
        {
            _http = true;
            var route = SliceScaffolder.RouteFor(_command!.TypeName);
            _lines.Add($"// The board gives {_command.TypeName} no apiEndpoint, so this is the route scaffold writes for it");
            emit("await WhenPosted(", addressed(_command), $", {quote(route)});");
        }

        /// <summary>The board's <c>apiEndpoint</c> as the act: the verb, the route with its holes filled, and the body when the route takes one.</summary>
        private void httpRequest(HttpRequest http)
        {
            _http = true;
            var method = http.Method.ToUpperInvariant();
            var (template, values) = routeOf(http);
            var arguments = values.Count == 0 ? "" : ", " + string.Join(", ", values);

            if (method == "GET")
            {
                // A GET is a read: of the read model the example expects, else of what the body names
                var read = _views.Count > 0 ? document(_views[0].Document.Label) : _command?.TypeName ?? "object";
                _lines.Add($"var response = await WhenReading<{read}>({quote(template)}{arguments});");
                return;
            }

            if (_command is null)
            {
                var bodyLess = method switch
                {
                    "PUT" => "WhenPutTo",
                    "PATCH" => "WhenPatchedTo",
                    "DELETE" => "WhenDeletedAt",
                    _ => "WhenPostedTo",
                };
                _lines.Add($"await {bodyLess}({quote(template)}{arguments});");
                return;
            }

            var verb = method switch
            {
                "PUT" => "WhenPut",
                "PATCH" => "WhenPatched",
                "DELETE" => "WhenDeleted",
                _ => "WhenPosted",
            };
            var route = values.Count == 0 ? quote(template) : $"HttpRoutes.Fill({quote(template)}{arguments})";
            emit($"await {verb}(", addressed(_command), $", {route});");
        }

        /// <summary>
        /// A route's holes, in order, as the values the example gives them: an identity's local where the value is
        /// one, else the text. <c>HttpRoutes.Fill</c> and the body-less acts fill the template from them in order.
        /// </summary>
        private (string Template, List<string> Values) routeOf(HttpRequest http)
        {
            var values = new List<string>();
            foreach (Match hole in Regex.Matches(http.Route, @"\{\*{0,2}([A-Za-z_]\w*)[^}]*\}"))
            {
                var name = hole.Groups[1].Value;
                var value = http.RouteValues.FirstOrDefault(x => sameName(x.Key, name)).Value
                            ?? http.Values.FirstOrDefault(x => sameName(x.Key, name)).Value;
                if (value is null)
                {
                    values.Add($"default /* {comment(name)} not given in the example */");
                    _context.Report.Add($"⚠ {where}: the route {http.Route} has a {{{name}}} the example gives no value for.");
                    continue;
                }

                values.Add(_ids.TryGetValue(value, out var id) ? id.Variable : quote(value));
            }

            return (http.Route, values);
        }

        /// <summary>Whether two names are one member's: <c>invoice id</c>, <c>invoiceId</c> and <c>InvoiceId</c> all are.</summary>
        private static bool sameName(string a, string b)
            => EmlangImport.PascalName(a).Equals(EmlangImport.PascalName(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A read model asked with parameters (an inline <c>QUERY</c>): read over HTTP at the route <c>scaffold</c>
        /// writes for the query, one hole per parameter, said so.
        /// </summary>
        private void query(ReadModelQueried queried)
        {
            _http = true;
            var typeName = document(queried.ReadModel);
            var route = SliceScaffolder.RouteFor(typeName);
            var arguments = new List<string>();
            foreach (var (parameter, value) in queried.Parameters)
            {
                var (member, type) = _context.Member(typeName, parameter);
                route += "/{" + char.ToLowerInvariant(member[0]) + member[1..] + "}";
                arguments.Add(value.Length > 0 && _ids.TryGetValue(value, out var id) ? id.Variable : Literal(type, value, null, _context.Ids));
            }

            _lines.Add($"// The board gives the {typeName} query no route, so this is the one scaffold writes for it");
            _lines.Add($"var response = await WhenReading<{typeName}>({quote(route)}{string.Concat(arguments.Select(x => ", " + x))});");
        }

        /// <summary>
        /// Time advancing: by the duration the source wrote, when it is one, with the scheduled messages then due
        /// executed; a named timer (an ESDM <c>timer</c>) fires as the scheduled messages executing.
        /// </summary>
        private void timeAdvances(string by)
        {
            if (Duration(by) is { } duration)
            {
                _lines.Add($"await WhenTimePasses({duration});");
                return;
            }

            _lines.Add($"// The model's timer \"{comment(by)}\" fires");
            _lines.Add("await WhenScheduledMessagesAreExecuted();");
        }

        // The key of the stream the givens were arranged on, which the act addresses
        private string? _actKey;

        // A stream the act starts under an id its handler mints, and the local that stands for it (bobcat#453)
        private (string Stream, string Key)? _startedKey;

        /// <summary>
        /// Whether the act's command can carry the stream's identity to its handler (bobcat#453): it has
        /// an <c>Id</c> (the conventional one, or one an example names), the stream's <c>{Aggregate}Id</c>
        /// or <c>AggregateId</c>, an identity the model marks, or an example value that is the key.
        /// A command that only starts a Guid-keyed stream has none of these, because its handler mints the id.
        /// </summary>
        private bool carriesIdentity(string stream, string key)
        {
            if (_command is null) return false;

            var typeName = _command.TypeName;
            if (CSharpModelWriter.GetsDefaultId(_context.Model, typeName)) return true;
            if (_context.IdentitiesOf(typeName).Count > 0) return true;
            if (CSharpModelWriter.FieldsOf(_context.Model, typeName).Any(f => f.Name is "Id" or "AggregateId" || f.Name == stream + "Id")) return true;
            if (_command.Values.Values.Any(x => _ids.TryGetValue(x, out var id) && id.Variable == key)) return true;

            return false;
        }

        /// <summary>
        /// The local that stood for a started stream's id becomes the id the act's handler assigned
        /// (bobcat#453), declared after the act: <c>var theApplication = TheStartedStream&lt;Application&gt;();</c>.
        /// Dropped again, like any minted local, when no expectation uses it.
        /// </summary>
        private void readStartedStreamFromAct()
        {
            if (_startedKey is not { } started) return;

            var declaration = $"var {started.Key} = ";
            _lines.RemoveAll(x => x.StartsWith(declaration, StringComparison.Ordinal));
            if (!_mintedLocals.Contains(started.Key)) _mintedLocals.Add(started.Key);
            _lines.Add($"{declaration}TheStartedStream<{started.Stream}>();");
        }

        // Each arranged stream (aggregate) -> its key, for a command addressing several (bobcat#444)
        private readonly Dictionary<string, string> _streamKeys = new(StringComparer.Ordinal);

        /// <summary>The slice whose command the example's command is, wherever on the model it is.</summary>
        private CuratedSlice? owner
            => _command is null ? null
                : _context.Model.Slices.FirstOrDefault(s => CSharpModelWriter.Identifiers.Sanitize(s.Command ?? "") == _command.TypeName);

        /// <summary>
        /// The command's partial, addressed to the arranged stream by its conventional <c>Id</c> when
        /// the model gives the command no identity of its own (bobcat#438). Without it the command's
        /// Guids were all random, so it could never reach the stream the assertion reads.
        /// </summary>
        private PartialSpec addressed(Ref command)
        {
            var built = partial(command);
            var typeName = built.TypeName;
            var owner = this.owner;

            // Several streams: each addressed by its own {Aggregate}Id (bobcat#444)
            var streamIds = CSharpModelWriter.StreamIdFieldsOf(_context.Model, typeName);
            if (streamIds.Count > 0 && owner is not null)
            {
                foreach (var aggregate in owner.Aggregates)
                {
                    var member = CSharpModelWriter.Identifiers.Sanitize(aggregate) + "Id";
                    if (!_streamKeys.TryGetValue(TypeName(aggregate), out var key)) continue;
                    if (built.Has(member)) continue;

                    _context.Member(typeName, member);
                    built.AddVariable(member, key);
                }

                return built;
            }

            if (_actKey is null || built.Has("Id")) return built;
            if (owner?.Dcb == true) return built;
            if (!CSharpModelWriter.GetsDefaultId(_context.Model, typeName)) return built;

            // A string-keyed stream's command carries a string Id (bobcat#453), and a strong-typed one
            // carries the identity type (bobcat#512) -- both of which Member would otherwise type Guid
            if (CSharpModelWriter.DefaultIdType(_context.Model, typeName, _context.Ids) is var idType && idType != "Guid")
            {
                if (!_context.ExtraFields.TryGetValue(typeName, out var extra)) _context.ExtraFields[typeName] = extra = [];
                if (extra.All(x => x.Name != "Id")) extra.Add(new CSharpModelWriter.StubField("Id", idType));
            }

            // The stream the command decides against, when the givens arranged more than one
            var actKey = owner?.Aggregates.Count == 1 && _streamKeys.TryGetValue(TypeName(owner.Aggregates[0]), out var own)
                ? own
                : _actKey;

            _context.Member(typeName, "id");
            built.AddVariable("Id", actKey);
            return built;
        }

        private void assert(bool arranged)
        {
            readStartedStreamFromAct();
            var acted = _model.Act is not null;

            foreach (var refusal in _refusals) refused(refusal);

            if (_nothingEmitted)
            {
                // dcb.events' "then nothing": accepted, and nothing appended
                _lines.Add("ThenTheActSucceeded();");
                _lines.Add("ThenNoEvents();");
            }

            // An automation's events are appended by the command its trigger caused, so they are read from the
            // act's cascade (ThenCaused), not the act's own appends (ThenEvents)
            var eventsVerb = _trigger is not null ? "ThenCaused(" : "ThenEvents(";

            foreach (var ignored in _emitted.Where(x => x.Ignored.Count > 0))
            {
                var members = ignored.Ignored.Select(x => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(x))).ToList();
                _lines.Add($"// {string.Join(", ", members)} {(members.Count == 1 ? "is" : "are")} generated by the handler, so the example's {(members.Count == 1 ? "value is" : "values are")} not compared");
            }

            // A command deciding against several arranged streams: each stream's events checked on it by its
            // own key, and a stream the act leaves alone said so (bobcat#444, wolverine#4865)
            var decidedAgainst = owner?.Aggregates.Select(TypeName).Where(_streamKeys.ContainsKey).ToList() ?? [];

            if (_emitted.Count > 0 && decidedAgainst.Count > 1)
            {
                foreach (var stream in decidedAgainst)
                {
                    var on = _emitted.Where(x => streamName(x) == stream).Select(partial).ToList();
                    if (on.Count == 0) _lines.Add($"{store}ThenNoEventsOn<{stream}>({_streamKeys[stream]});");
                    else call($"{store}ThenEventsOn<{stream}>({_streamKeys[stream]}, ", on, ");");
                }

                // An event the model puts on no stream the command decides against is still checked
                var elsewhere = _emitted.Where(x => streamName(x) is not { } stream || !decidedAgainst.Contains(stream)).Select(partial).ToList();
                if (elsewhere.Count > 0) call("ThenEmitted(", elsewhere, ");");
            }
            else if (_emitted.Count > 0 && !arranged && _actKey is null && _trigger is null
                     && _emitted.Select(streamName).Distinct().ToList() is [{ } started])
            {
                // The act starts the stream, with an id its handler assigns: one paragraph, "a VolunteerApplication
                // stream is started" and "VolunteerApplicationSubmitted is emitted on it" (wolverine#4865)
                // Asynchronous -- it reads the stream back -- so awaited, or the spec ends before it checks
                call($"await {store}ThenStreamIsStartedWithEvents<{started}>(", _emitted.Select(partial).ToList(), ");");
            }
            else if (_emitted.Count > 0)
            {
                call(eventsVerb, _emitted.Select(partial).ToList(), ");");
            }
            else if (acted && actsOnACommand && _views.Count == 0 && _caused.Count == 0 && !_nothingEmitted && _trigger is null)
            {
                // A refusal appends nothing, and neither does an act the model expects nothing from
                _lines.Add("ThenNoEvents();");
            }

            // The command an automation's trigger caused it to issue
            if (_trigger is not null && _command is not null)
            {
                _lines.Add(hasValues(_command)
                    ? rendered("ThenMessageSent(", partial(_command), ");")
                    : $"ThenMessageSent<{_command.TypeName}>();");
            }

            foreach (var (outcome, document, rows) in _views) readModel(outcome, document, rows);

            foreach (var (outcome, message) in _caused)
            {
                var typeName = message.TypeName;
                if (!_context.Elements.Contains(typeName)) _context.Elements.Add(typeName);
                _lines.Add((outcome.Scheduled, hasValues(message)) switch
                {
                    (true, true) => rendered("ThenMessageScheduled(", partial(message), ");"),
                    (true, false) => $"ThenMessageScheduled<{typeName}>();",
                    (false, true) => rendered("ThenMessageSent(", partial(message), ");"),
                    (false, false) => $"ThenMessageSent<{typeName}>();",
                });
            }

            if (_boundary is not null) boundary(_boundary);

            if (!acted && _emitted.Count == 0 && _refusals.Count == 0 && _views.Count == 0 && _caused.Count == 0 && !_nothingEmitted)
            {
                // A test of a view that names nothing to expect: the read model does not exist
                var view = _slice?.ReadModels.FirstOrDefault();
                if (view is null)
                {
                    _lines.Add("// TODO: the model expects nothing here, and the slice has no view to look for");
                    _context.Report.Add($"⚠ {where}: expects nothing, and its slice has no view.");
                    return;
                }

                var typeName = document(view);
                _lines.Add(anyIdentity() is { } key
                    ? $"await {store}ThenNoReadModel<{typeName}>({key});"
                    : $"await {store}ThenNoReadModel<{typeName}>();");
            }
        }

        /// <summary>
        /// A refusal by its kind: a validation failure, a not-found, a problem-details answer under an HTTP act,
        /// else the refusal with its reason and the values the reason names.
        /// </summary>
        private void refused(Refusal refusal)
        {
            var mentioned = refusal.Mentioned
                .Where(x => x.Length > 0)
                .Select(x => _ids.TryGetValue(x, out var id) ? id.Variable : quote(x))
                .ToList();
            var naming = string.Join(", ", new[] { quote(refusal.Reason) }.Concat(mentioned));

            switch (refusal.Kind)
            {
                case RefusalKind.Validation:
                    _lines.Add($"ThenValidationFails({quote(refusal.Reason)});");
                    break;

                case RefusalKind.NotFound when _http:
                    _lines.Add("ThenNotFound();");
                    break;

                case RefusalKind.NotFound:
                    _lines.Add("// The model says what the command addresses is not found");
                    _lines.Add($"ThenRefusedWith({naming});");
                    break;

                case RefusalKind.ProblemDetails or RefusalKind.Refused when _http && mentioned.Count == 0:
                    // An endpoint refuses with ProblemDetails, and the reason is its detail
                    _context.UsesProblemDetails = true;
                    _lines.Add($"await ThenRefusedWithProblem(Specify<ProblemDetails>().With(x => x.Detail, {quote(refusal.Reason)}));");
                    break;

                default:
                    // Holds for a message and for an endpoint both: the refusal's text, or its ProblemDetails, naming
                    // each value the reason mentions
                    _lines.Add($"ThenRefusedWith({naming});");
                    break;
            }
        }

        /// <summary>The read model after the act, by its shape: one document, the rows of a list, or none.</summary>
        private void readModel(ReadModelOutcome outcome, Ref view, List<Ref> rows)
        {
            var typeName = document(view.Label);

            switch (outcome.Shape)
            {
                case ReadModelShape.None:
                    _lines.Add(idOf(outcome, typeName, view) is { } none
                        ? $"await {store}ThenNoReadModel<{typeName}>({none});"
                        : $"await {store}ThenNoReadModel<{typeName}>();");
                    return;

                case ReadModelShape.Rows:
                    // Decided 2026-11-27: the rows are the whole answer, in any order; a source that says these
                    // among others is honoured
                    var set = outcome.Match == MatchMode.Subset ? "Expect.Containing(" : "Expect.InAnyOrder(";
                    call($"await {store}ThenDocuments<{typeName}>({set}", rows.Select(partial).ToList(), "));");
                    return;
            }

            // No identity in the example: a singleton view, the only one of its type
            var key = idOf(outcome, typeName, view);
            if (key is null)
            {
                _context.Report.Add($"{where}: the {typeName} view names no identity, so it is checked as the only {typeName}.");

                // Said in the spec too, never assumed silently (bobcat#450): a view keyed per stream
                // needs ThenReadModel<T>(id, ...) instead
                _lines.Add($"// The example names no {typeName} identity, so this checks the only one. If there can be");
                _lines.Add($"// several, name the one this example means: await ThenReadModel<{typeName}>(id, ...);");
                _lines.Add(!hasValues(view)
                    ? $"await {store}ThenSingleReadModel<{typeName}>();"
                    : rendered($"await {store}ThenSingleReadModel<{typeName}>(", partial(view), ");"));
                return;
            }

            _lines.Add(!hasValues(view)
                ? $"await {store}ThenReadModel<{typeName}>({key});"
                : rendered($"await {store}ThenReadModel<{typeName}>({key}, ", partial(view), ");"));
        }

        /// <summary>The document's key: the id the outcome names apart from its values, else the identity its values name, else the test's.</summary>
        private string? idOf(ReadModelOutcome outcome, string typeName, Ref view)
        {
            if (outcome.Id is { Length: > 0 } id) return _ids.TryGetValue(id, out var local) ? local.Variable : quote(id);
            return outcome.Shape == ReadModelShape.None && view.Values.Count == 0 && _ids.Count == 0 ? null : documentKey(typeName, view);
        }

        /// <summary>
        /// A DCB slice's boundary, as far as the model says it: the query the act reads, and the events that would or
        /// would not conflict with it, each on the tags the example names.
        /// </summary>
        private void boundary(BoundaryExpectation expectation)
        {
            _context.UsesEventTagQuery = true;
            var tags = tagLocals();

            if (expectation.Query is { Length: > 0 } query) _lines.Add($"// The model expects the act to read the boundary: {comment(query)}");
            if (tags.Count == 0)
            {
                _lines.Add("// TODO: the example names no tag value to build the boundary query from");
                _context.Report.Add($"⚠ {where}: expects a consistency boundary, but names no tag value to build the query from.");
            }
            else
            {
                var types = _given.Concat(_emitted).Select(x => x.TypeName).Distinct().ToList();
                var ofType = types.Count == 0 ? "" : $".AndEventsOfType<{string.Join(", ", types)}>()";
                _lines.Add($"ThenTheConsistencyBoundaryIs(EventTagQuery.For({string.Join(", ", tags)}){ofType});");
            }

            foreach (var conflicting in expectation.Conflicting)
            {
                var typeName = TypeName(conflicting);
                if (!_context.Elements.Contains(typeName)) _context.Elements.Add(typeName);
                _lines.Add($"ThenAConcurrentEventWouldConflict<{typeName}>({string.Join(", ", tags)});");
            }

            foreach (var notConflicting in expectation.NotConflicting)
            {
                var typeName = TypeName(notConflicting);
                if (!_context.Elements.Contains(typeName)) _context.Elements.Add(typeName);
                _lines.Add($"ThenAConcurrentEventWouldNotConflict<{typeName}>({string.Join(", ", tags)});");
            }
        }

        /// <summary>The distinct tag values the example's events carry, as the locals or literals that stand for them.</summary>
        private List<string> tagLocals()
        {
            var locals = new List<string>();
            foreach (var reference in _given.Concat(_emitted))
            {
                foreach (var tag in tagExpressions(reference))
                {
                    if (!locals.Contains(tag)) locals.Add(tag);
                }
            }

            return locals;
        }

        /// <summary>An event's tag values as expressions: an identity's local, else the value in the type of the member named for the tag.</summary>
        private List<string> tagExpressions(Ref reference)
        {
            var expressions = new List<string>();
            foreach (var (tag, value) in reference.Tags)
            {
                if (value.Length == 0) continue;
                if (_ids.TryGetValue(value, out var id))
                {
                    expressions.Add(id.Variable);
                    continue;
                }

                var (_, type) = _context.Member(reference.TypeName, tag, reference.Sample(tag));
                expressions.Add(Literal(type, value, reference.Raw.GetValueOrDefault(tag), _context.Ids));
            }

            return expressions;
        }

        // The column a test body's statements start at: two levels in, class then method
        private const int Body = 8;

        private static string rendered(string open, PartialSpec argument, string close)
            => open + argument.Render(Body + open.Length, close.Length) + close;

        private void call(string open, IReadOnlyList<PartialSpec> arguments, string close)
        {
            if (arguments.Count == 1)
            {
                _lines.Add(rendered(open, arguments[0], close));
                return;
            }

            _lines.Add(open.TrimEnd().TrimEnd(','));
            if (!_lines[^1].EndsWith('(')) _lines[^1] += ",";
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i].Render(Body + 4);
                _lines.Add("    " + argument.Replace("\n", "\n    ") + (i < arguments.Count - 1 ? "," : close));
            }
        }

        private PartialSpec partial(Ref reference)
        {
            var typeName = reference.TypeName;
            if (!_context.Elements.Contains(typeName)) _context.Elements.Add(typeName);
            var spec = new PartialSpec(typeName);

            foreach (var (prop, text) in reference.Values)
            {
                // A generated member is the handler's to fill, so the example's value is not compared
                if (reference.Ignored.Contains(prop, StringComparer.OrdinalIgnoreCase)) continue;

                var raw = reference.Raw.GetValueOrDefault(prop);
                var (member, type) = _context.Member(typeName, prop, reference.Sample(prop));
                if (text.Length > 0 && _ids.TryGetValue(text, out var id) && (id.Type == type || type == "string"))
                {
                    spec.Add(member, id.Type == type ? id.Variable : $"{id.Variable}.ToString()", "{{" + id.Variable + "}}");
                    continue;
                }

                spec.Add(member, Literal(type, text, raw, _context.Ids), Cell(type, text, raw, _context.Ids));
            }

            foreach (var (member, variable) in _minted.GetValueOrDefault(reference) ?? [])
            {
                spec.AddVariable(member, variable);
            }

            foreach (var tag in tagExpressions(reference)) spec.Tag(tag);

            return spec;
        }

        private string? streamOf(Ref reference)
        {
            var stream = streamName(reference);
            if (stream is not null) _context.Streams.Add(stream);
            return stream;
        }

        /// <summary>The stream's identity in an event: <c>{Stream}Id</c>, or else the first identity it names.</summary>
        private string? identityOf(string? stream, Ref reference)
        {
            string? fallback = null;
            foreach (var (prop, text) in reference.Values)
            {
                var (member, _) = _context.Member(reference.TypeName, prop, reference.Sample(prop));
                if (!_ids.TryGetValue(text, out var id)) continue;

                if (stream is not null && member == stream + "Id") return id.Variable;
                fallback ??= member.EndsWith("Id", StringComparison.Ordinal) ? id.Variable : null;
            }

            return fallback;
        }

        /// <summary>
        /// The stream's key in this test: the identity the event names, else the one already minted
        /// for the stream, else a fresh one minted here (bobcat#438). The act addresses that same
        /// local through the command's <c>Id</c>, so an identity the model never names is no longer
        /// a TODO: it is simply a value only the test needs to know.
        /// </summary>
        private string keyOf(string? stream, Ref reference)
        {
            if (identityOf(stream, reference) is { } key) return key;

            var owner = stream ?? "Stream";
            if (_streams.TryGetValue(owner, out var minted)) return minted;

            var variable = mint(owner, _context.Ids.IdOf(owner) ?? "Guid");
            _streams[owner] = variable;
            return variable;
        }

        /// <summary>A read model's type, recorded as a document the stubs give an id.</summary>
        private string document(string label)
        {
            var typeName = TypeName(label);
            _context.Documents.Add(typeName);
            if (!_context.Elements.Contains(typeName)) _context.Elements.Add(typeName);
            return typeName;
        }

        /// <summary>The identity a view's example names, or else the test's first; null when there is none.</summary>
        private string? documentKey(string typeName, Ref view)
        {
            foreach (var (prop, text) in view.Values)
            {
                var (member, _) = _context.Member(typeName, prop, view.Sample(prop));
                if (member.EndsWith("Id", StringComparison.Ordinal) && _ids.TryGetValue(text, out var id)) return id.Variable;
            }

            return anyIdentity();
        }

        /// <summary>The test's first identity, which is usually the stream the view is projected from.</summary>
        private string? anyIdentity()
            => _ids.Values.FirstOrDefault(x => _context.Primitive(x.Type) == "Guid").Variable;

        /// <summary>
        /// A view arranged directly: its example as a partial, with the document's <c>Id</c> set to the
        /// identity it is keyed by, so the specs that read it back find it.
        /// </summary>
        private PartialSpec documentPartial(string typeName, Ref view)
        {
            var built = partial(view);
            if (!built.Has("Id") && documentKey(typeName, view) is { } key) built.AddVariable("Id", key, first: true);
            return built;
        }
    }

    /// <summary>
    /// The most <c>.With(...)</c> calls a partial object is written with. One that names more is written as
    /// a <c>Property | Value</c> table instead, which reads far better than a chain that runs off the screen.
    /// </summary>
    public const int MaxWithCalls = 3;

    /// <summary>The margin a generated <c>.With(...)</c> chain wraps at, one member to a line.</summary>
    public const int MaxLineLength = 120;

    /// <summary>
    /// One partial object in a specification: its members in the order named, each with the C#
    /// expression a <c>.With(...)</c> chain writes and the cell text a table writes, and the DCB tags it
    /// carries (<c>.Tagged(...)</c>).
    /// </summary>
    private sealed class PartialSpec(string typeName)
    {
        // Cell is null where the value has no faithful table form, which keeps the whole object a chain
        private readonly List<(string Member, string Expression, string? Cell)> _members = [];
        private readonly List<string> _tags = [];

        public string TypeName => typeName;

        public bool Has(string member) => _members.Any(x => x.Member == member);

        public void Add(string member, string expression, string? cell) => _members.Add((member, expression, cell));

        /// <summary>A tag value the event carries, appended as <c>.Tagged(...)</c>.</summary>
        public void Tag(string expression) => _tags.Add(expression);

        /// <summary>A member set to a local, which a table writes as an interpolation.</summary>
        public void AddVariable(string member, string variable, bool first = false)
        {
            var entry = (member, variable, "{{" + variable + "}}");
            if (first) _members.Insert(0, entry);
            else _members.Add(entry);
        }

        private string tagged => _tags.Count == 0 ? "" : $".Tagged({string.Join(", ", _tags)})";

        /// <param name="column">The column the partial starts at, so a chain that would run past the margin wraps.</param>
        /// <param name="trailing">What follows the partial on its line, such as <c>);</c> or a route argument.</param>
        public string Render(int column = 0, int trailing = 2)
        {
            var oneLine = $"Specify<{typeName}>()" + string.Concat(_members.Select(x => $".With(x => x.{x.Member}, {x.Expression})")) + tagged;

            // A chain of two or more that fits stays on one line; a longer one, or a value with no
            // faithful cell form past the table threshold, puts each member on its own line
            var wraps = _members.Count >= 2 && column + oneLine.Length + trailing > MaxLineLength;
            if (_members.Count <= MaxWithCalls && !wraps) return oneLine;

            if (_members.Count <= MaxWithCalls || _members.Any(x => x.Cell is null))
            {
                return $"Specify<{typeName}>()" + string.Concat(_members.Select(x => $"\n    .With(x => x.{x.Member}, {x.Expression})"))
                       + (_tags.Count == 0 ? "" : "\n    " + tagged);
            }

            var width = Math.Max("Property".Length, _members.Max(x => x.Member.Length));
            var valueWidth = Math.Max("Value".Length, _members.Max(x => x.Cell!.Length));
            var interpolated = _members.Any(x => x.Cell!.Contains("{{", StringComparison.Ordinal));

            var builder = new StringBuilder($"Specify<{typeName}>({(interpolated ? "$$" : "")}\"\"\"\n");
            builder.Append($"    | {"Property".PadRight(width)} | {"Value".PadRight(valueWidth)} |\n");
            foreach (var (member, _, cell) in _members)
            {
                builder.Append($"    | {member.PadRight(width)} | {cell!.PadRight(valueWidth)} |\n");
            }

            builder.Append("    \"\"\")");
            builder.Append(tagged);
            return builder.ToString();
        }
    }

    /// <summary>
    /// An example value as table cell text, read back by the cell rules to the same value
    /// <see cref="Literal"/> writes; null when it has no faithful cell form.
    /// </summary>
    public static string? Cell(string type, string text, object? raw = null) => Cell(type, text, raw, StrongTypedIds.None);

    /// <inheritdoc cref="Cell(string, string, object?)"/>
    /// <param name="ids">
    /// The strong-typed identifiers in force. A typed identity has NO cell form here (bobcat#512): the
    /// value is written as code instead -- which is what a null answer here means. (<c>CellValues</c> can
    /// read an <c>OrderId</c> since bobcat#542, through its <c>IParsable</c>; this writer has not
    /// changed to use that.)
    /// </param>
    public static string? Cell(string type, string text, object? raw, StrongTypedIds ids)
    {
        if (ids.Contains(type)) return null;

        var trimmed = text.Trim();
        if (type == "string")
        {
            if (text.Length == 0) return "EMPTY";

            // A cell is trimmed and split on pipes, NULL and EMPTY are keywords, and braces would
            // read as an interpolation in the $$ raw string
            if (text != trimmed || text.IndexOfAny(['|', '\n', '\r', '{', '}', '"']) >= 0) return null;
            if (text is "NULL" or "EMPTY") return null;
            return text;
        }

        var literal = Literal(type, text, raw);
        if (literal.StartsWith("default", StringComparison.Ordinal) || literal.Contains("/*", StringComparison.Ordinal)) return null;

        return type switch
        {
            "int" or "long" or "decimal" or "double" or "bool" or "Guid" or "DateTimeOffset" or "DateOnly" or "TimeSpan"
                when trimmed.IndexOfAny(['|', '{', '}', '"']) < 0 => trimmed,
            _ => null
        };
    }

    /// <summary>An element's type name, exactly as the stub writer names its stub.</summary>
    public static string TypeName(string label)
        => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(label));

    /// <summary>A C# expression of <paramref name="type"/> for an example value.</summary>
    public static string Literal(string type, string text, object? raw = null) => Literal(type, text, raw, StrongTypedIds.None);

    /// <inheritdoc cref="Literal(string, string, object?)"/>
    /// <param name="ids">
    /// The strong-typed identifiers in force: a typed identity is its inner value wrapped in the type
    /// (bobcat#512), so the one rule for reading an example value is still the primitive's.
    /// </param>
    public static string Literal(string type, string text, object? raw, StrongTypedIds ids)
    {
        if (ids.Inner(type) is { } inner) return $"new {type}({Literal(inner, text, raw, ids)})";

        var trimmed = text.Trim();

        switch (type)
        {
            case "string":
                return quote(text);

            case CuratedFieldTypes.StringList:
                // The board kept the list where it had one; the scenario model has its text, the items joined
                // with ", " (EmlangReader.Text), which splits back the same way
                var items = raw is List<object> list
                    ? list.Select(EmlangReader.Text)
                    : trimmed.Length == 0 ? [] : trimmed.Split(", ", StringSplitOptions.None);
                var quoted = items.Select(quote).ToList();
                return quoted.Count == 0 ? "new List<string>()" : $"new List<string> {{ {string.Join(", ", quoted)} }}";
        }

        if (trimmed.Length == 0) return "default";

        return type switch
        {
            "int" when int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i.ToString(CultureInfo.InvariantCulture),
            "long" when long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l.ToString(CultureInfo.InvariantCulture) + "L",
            "decimal" when decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) => m.ToString(CultureInfo.InvariantCulture) + "m",
            "double" when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            "bool" when bool.TryParse(trimmed, out var b) => b ? "true" : "false",
            "Guid" when Guid.TryParse(trimmed, out _) => $"Guid.Parse({quote(trimmed)})",
            "Guid" => $"Guid.CreateVersion7() /* {comment(trimmed)} in the model */",
            "DateTimeOffset" when DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"DateTimeOffset.Parse({quote(trimmed)})",
            "DateOnly" when DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"DateOnly.Parse({quote(trimmed)})",
            "TimeSpan" when TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out _) => $"TimeSpan.Parse({quote(trimmed)})",
            _ => $"default /* {comment(trimmed)} in the model */"
        };
    }

    private static readonly Regex Words = new(
        @"^(?<n>\d+(?:\.\d+)?)\s*(?<u>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|wks?|w)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A duration as a source writes it -- <c>2 days</c>, <c>30m</c>, <c>P1D</c>, <c>1.00:00:00</c> -- as the
    /// <c>TimeSpan</c> expression that reads best for it; null when the text is not a duration.
    /// </summary>
    public static string? Duration(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        TimeSpan span;
        if (Words.Match(trimmed) is { Success: true } words)
        {
            var amount = double.Parse(words.Groups["n"].Value, CultureInfo.InvariantCulture);
            span = words.Groups["u"].Value.ToLowerInvariant()[0] switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => TimeSpan.FromDays(amount * 7),
            };
        }
        else if (trimmed.StartsWith('P') || trimmed.StartsWith("-P", StringComparison.Ordinal))
        {
            try
            {
                span = XmlConvert.ToTimeSpan(trimmed);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        else if (!TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out span))
        {
            return null;
        }

        if (span < TimeSpan.Zero) return null;
        if (span.Ticks % TimeSpan.TicksPerDay == 0) return $"TimeSpan.FromDays({span.TotalDays.ToString(CultureInfo.InvariantCulture)})";
        if (span.Ticks % TimeSpan.TicksPerHour == 0) return $"TimeSpan.FromHours({span.TotalHours.ToString(CultureInfo.InvariantCulture)})";
        if (span.Ticks % TimeSpan.TicksPerMinute == 0) return $"TimeSpan.FromMinutes({span.TotalMinutes.ToString(CultureInfo.InvariantCulture)})";
        if (span.Ticks % TimeSpan.TicksPerSecond == 0) return $"TimeSpan.FromSeconds({span.TotalSeconds.ToString(CultureInfo.InvariantCulture)})";
        return $"TimeSpan.Parse({quote(span.ToString("c", CultureInfo.InvariantCulture))})";
    }

    private static string unique(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }

    private static string snake(string text)
    {
        var words = Regex.Matches(text, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")
            .Select(x => x.Value.ToLowerInvariant())
            .ToList();

        var name = string.Join("_", words);
        if (name.Length == 0) return "unnamed";
        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    private static string quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private static string comment(string value) => value.Replace("*/", "* /").Replace("\n", " ");
}
