using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JasperFx.CodeGeneration;

using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;

namespace Wolverine.Bobcat.EventModel;

/// <summary>What <see cref="EmlangSpecWriter.Write"/> produced.</summary>
/// <param name="Files">
/// The specification files, each relative to the spec project (bobcat#440): <c>TestSupport.cs</c>
/// with the fixture, the collection and the base spec class, then one file per slice under a folder
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
/// Writes an emlang model's examples as WolverineFx.Bobcat specifications (bobcat#423): one
/// <c>[BobcatFeature]</c> class per slice, one <c>[Fact]</c> per test, each value the example
/// gives as a partial object naming exactly those members.
/// </summary>
/// <remarks>
/// <para>
/// <b>A specification's identity is the one the model links (bobcat#435).</b> The feature is the
/// SLICE the import attached the test to, not the board chapter it was written under, and the
/// scenario is <see cref="ScenarioTitle"/>, the method name read back as a sentence, which is what
/// a projected test reports. <c>EmlangImport</c> names each scenario the same way, so the
/// definition's <c>LinksToSpecification</c>, the pushed descriptor and a run's
/// <c>scenario_finished</c> all carry one string. Before this, the features were chapters and
/// the definition linked the board's spelling, so not one generated specification joined its slice.
/// </para>
/// <para>
/// <b>The code targets WolverineFx.Bobcat's <c>WolverineSpec</c></b>, which this package does not
/// reference: it is text, and the project it lands in references WolverineFx.Bobcat.
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
/// specification for each new example to the existing class (<see cref="ImportMerge.MergeSpecs"/>), and
/// <see cref="MissingSpecs"/> reports the model's tests that still have none.
/// </para>
/// </remarks>
public static class EmlangSpecWriter
{
    /// <summary>The file holding the fixture, the collection and the base specification class.</summary>
    public const string TestSupportFile = "TestSupport.cs";

    /// <summary>The spec project's global usings, so each specification file holds only its specification.</summary>
    public const string GlobalUsingsFile = "GlobalUsings.cs";

    /// <param name="board">The board the model was imported from: its tests are the examples.</param>
    /// <param name="model">The imported model.</param>
    /// <param name="ns">The application's root namespace, where the stubs and the definition live.</param>
    /// <param name="specsNamespace">
    /// The spec project's root namespace, which should be the spec project's name; each chapter
    /// folder adds a segment. Defaults to <c>{ns}.Specs</c>.
    /// </param>
    /// <param name="store">
    /// The application's event store: <c>marten</c>, <c>polecat</c> or <c>fisher</c>. It decides how the
    /// generated host runs the store's async daemon; when null the host names each store's way in a comment.
    /// </param>
    /// <inheritdoc cref="Write(EmlangBoard, ImportedEventModel, string, string?, string?)"/>
    /// <param name="identities">
    /// The strong-typed identifiers the stubs are written with (bobcat#512/#433), so a specification
    /// declares an identity as the typed value — <c>var theOrder = new OrderId(Guid.CreateVersion7());</c>
    /// — and every member that takes it compiles. <see cref="StrongTypedIds.None"/>, or null, keeps the
    /// Guid it always was.
    /// </param>
    public static GeneratedSpecs Write(EmlangBoard board, ImportedEventModel model, string ns, string? specsNamespace,
        string? store, StrongTypedIds? identities)
        => write(board, model, ns, specsNamespace, store, identities ?? StrongTypedIds.None);

    /// <inheritdoc cref="Write(EmlangBoard, ImportedEventModel, string, string?, string?, StrongTypedIds?)"/>
    public static GeneratedSpecs Write(EmlangBoard board, ImportedEventModel model, string ns, string? specsNamespace = null,
        string? store = null)
        => write(board, model, ns, specsNamespace, store, StrongTypedIds.None);

    private static GeneratedSpecs write(EmlangBoard board, ImportedEventModel model, string ns, string? specsNamespace,
        string? store, StrongTypedIds identities)
    {
        var context = new Context(model, identities);
        context.Declare(board);
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
            var scenarios = slice.Specifications?.Scenarios.Where(x => x.SourceOf() is not null).ToList() ?? [];
            if (scenarios.Count == 0)
            {
                context.Report.Add($"slice '{slice.Name}': no tests, so no specifications.");
                continue;
            }

            // A slice and one of its examples often share a name (a view slice and the test of that
            // view), and C# refuses a member named for its enclosing type (CS0542). The class name is
            // not part of the identity, [BobcatFeature] is, so the class gives way, never the method.
            var methodNames = scenarios.Select(x => MethodName(x.SourceOf()!.Name)).ToHashSet(StringComparer.Ordinal);
            var classCandidate = FeatureClassName(slice.Name);
            if (methodNames.Contains(classCandidate)) classCandidate += "_feature";
            var className = unique(classNames, classCandidate);
            features++;

            using var writer = new SourceWriter();
            if (slice.Pattern == "Automation" && slice.Trigger?.Label is { Length: > 0 } trigger)
            {
                // The examples exercise the command; what triggers it is wiring they never reach
                var said = $"// {slice.Name} is an automation, triggered by \"{comment(trigger)}\"";
                if (said.Length <= MaxLineLength)
                {
                    writer.WriteLine(said);
                }
                else
                {
                    writer.WriteLine($"// {slice.Name} is an automation, triggered by");
                    writer.WriteLine($"// \"{comment(trigger)}\"");
                }
            }

            writer.WriteLine($"[BobcatFeature({quote(slice.Name)})]");
            writer.WriteLine(sliceBinding(slice, context));
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}Spec(app)");

            var first = true;
            foreach (var scenario in scenarios)
            {
                if (!first) writer.BlankLine();
                first = false;

                // The import already deduplicated the slice's scenarios by title, and a title
                // round-trips to exactly one method name, so no method here needs a suffix
                writer.WriteLine("[Fact]");
                writer.Write($"BLOCK:public async Task {MethodName(scenario.SourceOf()!.Name)}()");
                foreach (var line in new TestWriter(context, slice.Name, scenario.SourceChapterOf()!, scenario.SourceOf()!).Lines())
                {
                    if (line.Length == 0) writer.BlankLine();
                    else writer.WriteLine(line);
                }
                writer.FinishBlock();
                specs++;
            }

            writer.FinishBlock();

            var fileName = CSharpModelWriter.Identifiers.Sanitize(slice.Name);
            bodies.Add((ModelLayout.ChapterFolder(slice.Chapter), fileName.Length == 0 ? className : fileName, writer.Code()));
        }

        // A test the import attached to no slice still gets its specification, under its chapter as
        // before: what it arranges and asserts is still worth writing, and its examples are still
        // fields the stubs need. It binds to nothing on the model, and the report says so.
        var attached = model.Slices
            .SelectMany(x => x.Specifications?.Scenarios ?? [])
            .Select(x => x.SourceOf())
            .OfType<EmlangTest>()
            .ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var chapter in board.Chapters)
        {
            var orphans = chapter.Tests.Where(x => !attached.Contains(x)).ToList();
            if (orphans.Count == 0) continue;

            var className = unique(classNames, FeatureClassName(chapter.Name));
            features++;

            using var writer = new SourceWriter();
            writer.WriteLine($"// The model attaches these to no slice, so they bind to nothing on the event model");
            writer.WriteLine($"[BobcatFeature({quote(chapter.Name)})]");
            writer.Write($"BLOCK:public class {className}(AppFixture app) : {modelName}Spec(app)");

            var methods = new HashSet<string>(StringComparer.Ordinal);
            var first = true;
            foreach (var test in orphans)
            {
                if (!first) writer.BlankLine();
                first = false;

                context.Report.Add($"⚠ chapter '{chapter.Name}', test '{test.Name}': attached to no slice, so its specification binds to nothing on the model.");
                writer.WriteLine("[Fact]");
                writer.Write($"BLOCK:public async Task {unique(methods, MethodName(test.Name))}()");
                foreach (var line in new TestWriter(context, chapter.Name, chapter, test).Lines())
                {
                    if (line.Length == 0) writer.BlankLine();
                    else writer.WriteLine(line);
                }
                writer.FinishBlock();
                specs++;
            }

            writer.FinishBlock();
            var folder = ModelLayout.ChapterFolder(chapter.Name);
            bodies.Add((folder, (folder ?? "Model") + "Unattached", writer.Code()));
        }

        var additions = new CSharpModelWriter.StubAdditions(context.Streams.ToList(), context.Documents.ToList(), context.Elements.ToList(),
            context.ExtraFields.ToDictionary(x => x.Key, x => (IReadOnlyList<CSharpModelWriter.StubField>)x.Value));

        // Every namespace the stubs land in: a spec names types from any chapter, and an unused
        // using costs nothing where a missing one does not compile
        var appNamespaces = CSharpModelWriter.Layout(model, ns, additions).Namespaces;

        var files = new List<GeneratedFile>
        {
            new(GlobalUsingsFile, writeGlobalUsings(appNamespaces)),
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

    /// <summary>
    /// bobcat#449: the spec's slice, by the type that bears its name where one does — the command, or
    /// a view's read model — so the IDE navigates from the spec to it and a rename keeps the two
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
                .Where(x => x.SourceOf() is { } source && !written.Contains((slice.Name, MethodName(source.Name))))
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
    private static string writeGlobalUsings(IReadOnlyList<string> appNamespaces)
    {
        var builder = new StringBuilder();
        builder.Append("// Generated by `write-specs` (WolverineFx.Bobcat.EventModel). Nothing regenerates this file.\n");
        builder.Append("//\n");
        builder.Append("// The specifications hold one test per example in the model, each value a partial object that\n");
        builder.Append("// names only what the example names. Run the import again and it adds a specification for each\n");
        builder.Append("// new example, without touching the ones here.\n");
        builder.Append('\n');
        foreach (var ns in new[] { "System", "System.Threading.Tasks", "Bobcat", "Wolverine.Bobcat", "Xunit" }.Concat(appNamespaces))
        {
            builder.Append($"global using {ns};\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>TestSupport.cs</c> (bobcat#440): the fixture, the collection and the base specification
    /// class, kept out of the specification files so the host is configured in one file that holds
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// The fixture is the application's own host, started through Alba as it is configured in
    /// production, with Wolverine in solo mode and external transports off. Nothing in the
    /// specifications depends on the store: <c>WolverineSpec</c> works against the application's
    /// <c>IEventStore</c>, and <c>ResetAsync</c> resets every event store the host registers. Only how
    /// the store's async daemon runs differs, which <paramref name="store"/> decides.
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
        return writer.Code();
    }

    private sealed class Context(ImportedEventModel model, StrongTypedIds identities)
    {
        /// <summary>The strong-typed identifiers the stubs use, so a spec's locals take the same types.</summary>
        public StrongTypedIds Ids { get; } = identities;

        /// <summary>
        /// The primitive a member's type IS: a strong-typed identity's inner <c>Guid</c> or
        /// <c>string</c>, else the type itself. Every decision about how to render or declare a value
        /// asks this, so <c>OrderId</c> takes exactly the path <c>Guid</c> takes.
        /// </summary>
        public string Primitive(string type) => Ids.Inner(type) ?? type;

        private readonly Dictionary<string, (string? Stream, List<string> Identities, List<string> Props)> _declared = new(StringComparer.Ordinal);

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

                var typeName = TypeName(step.Label);
                // bobcat#439: only a stream the model DECLARES. An emlang swimlane names who acts
                // (Admin / Volunteer approved), not where the event is stored, and reading it as a
                // stream spread one application's events over Admin, Volunteer and ShelterStaff.
                var stream = step.Stream is { Length: > 0 } declared ? declared : null;

                if (!_declared.TryGetValue(typeName, out var known))
                {
                    _declared[typeName] = (stream, [.. step.Identities], [.. step.Props.Keys]);
                    continue;
                }

                if (known.Stream is null && stream is not null) _declared[typeName] = known with { Stream = stream };
                foreach (var identity in step.Identities.Where(x => !known.Identities.Contains(x))) known.Identities.Add(identity);
                foreach (var prop in step.Props.Keys.Where(x => !known.Props.Contains(x))) known.Props.Add(prop);
            }
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
                : CuratedFieldTypes.TryInfer(EventModelInternals.Text(sample), out var inferred) ? inferred
                : "string";
            extra.Add(new CSharpModelWriter.StubField(name, type));
            return (name, type);
        }
    }

    /// <summary>One test's body. A class rather than a method because a test accumulates state as it goes: its identities.</summary>
    private sealed class TestWriter(Context context, string slice, EmlangChapter chapter, EmlangTest test)
    {
        private readonly List<string> _lines = [];

        // An identity's example value ("order-123") → the local that stands for it
        private readonly Dictionary<string, (string Variable, string Type)> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);

        private static readonly EmlangElementKind[] Elements =
            [EmlangElementKind.Command, EmlangElementKind.Event, EmlangElementKind.View];

        private string where => $"slice '{slice}', test '{test.Name}'";

        public IEnumerable<string> Lines()
        {
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
                                           && System.Text.RegularExpressions.Regex.IsMatch(x, $@"\b{variable}\b"));
                if (!used) _lines.RemoveAll(x => x.StartsWith(declaration, StringComparison.Ordinal));
            }

            if (_lines.Count > 0 && _lines[0].Length == 0) _lines.RemoveAt(0);
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);

            // A partial written as a table spans several lines
            return _lines.SelectMany(x => x.Split('\n'));
        }

        /// <summary>One local per identity the example names: typed ones (Guids) first, then string ids.</summary>
        private void declareIdentities()
        {
            var references = test.Given.Concat(test.When).Concat(test.Then).Where(x => Elements.Contains(x.Kind)).ToList();

            // A strong-typed identity takes the pass of what it wraps (bobcat#512): OrderId is a Guid
            // identity that happens to say so in the type system, not a third kind of value
            foreach (var wanted in new[] { "Guid", "string" })
            {
                foreach (var reference in references)
                {
                    var typeName = TypeName(reference.Label);
                    foreach (var (prop, value) in reference.Props)
                    {
                        var (member, type) = context.Member(typeName, prop, reference.Values.GetValueOrDefault(prop));
                        if (context.Primitive(type) != wanted || value.Length == 0 || _ids.ContainsKey(value)) continue;
                        if (type == "string" && !member.EndsWith("Id", StringComparison.Ordinal)) continue;

                        declare(member, value, type);
                    }
                }
            }

            // An example that names a stream's identity is that stream's local, for every element
            // on the stream that leaves its own identity out
            foreach (var reference in references)
            {
                var typeName = TypeName(reference.Label);
                if (streamName(reference) is not { } stream || _streams.ContainsKey(stream)) continue;

                foreach (var (prop, value) in reference.Props)
                {
                    if (!identityProp(typeName, stream, prop)) continue;
                    if (_ids.TryGetValue(value, out var id)) _streams[stream] = id.Variable;
                }
            }

            // The model marks an identity but the example gives it no value (bobcat#433 discussion,
            // case C): mint one local per stream, and set it on every element that names none
            foreach (var reference in references)
            {
                var typeName = TypeName(reference.Label);
                var stream = streamName(reference);

                foreach (var prop in context.IdentitiesOf(typeName))
                {
                    if (reference.Props.TryGetValue(prop, out var given) && given.Length > 0) continue;

                    var (member, type) = context.Member(typeName, prop);
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
        private readonly Dictionary<EmlangRef, List<(string Member, string Variable)>> _minted = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The stream (aggregate type) of a reference's element: what the model declares, else what
        /// the import inferred from the examples (bobcat#444). Null only when neither names one, and
        /// then the events go on a stream with no aggregate type, which Marten, Polecat and Fisher all
        /// allow (bobcat#439). Never the swimlane, which is an actor.
        /// </summary>
        private string? streamName(EmlangRef reference)
            => context.StreamOf(TypeName(reference.Label)) is { Length: > 0 } declared ? TypeName(declared) : null;

        private bool identityProp(string typeName, string stream, string prop)
        {
            if (context.IdentitiesOf(typeName).Contains(prop)) return true;
            if (EmlangImport.PascalName(prop) == "AggregateId") return true;
            return CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(prop)) == stream + "Id";
        }

        private readonly List<string> _mintedLocals = [];

        /// <summary>Whether a reference has anything to put in a partial: example values, or a minted identity.</summary>
        private bool hasValues(EmlangRef reference) => reference.Props.Count > 0 || _minted.ContainsKey(reference);

        /// <summary>A local for an identity the example never gives a value.</summary>
        private string mint(string owner, string type)
        {
            var name = "the" + owner;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;
            _mintedLocals.Add(variable);

            _lines.Add(context.Primitive(type) == "Guid"
                ? $"var {variable} = {wrap(type, "Guid.CreateVersion7()")};"
                : $"var {variable} = {wrap(type, quote(EmlangImport.PascalName(owner).ToLowerInvariant() + "-1"))};");
            return variable;
        }

        /// <summary>
        /// An identity value in its declared type: <c>new OrderId(...)</c> for a strong-typed identifier
        /// (bobcat#512), the expression itself for a primitive one.
        /// </summary>
        private string wrap(string type, string expression)
            => context.Ids.Contains(type) ? $"new {type}({expression})" : expression;

        private void declare(string member, string value, string type)
        {
            var stem = member.EndsWith("Id", StringComparison.Ordinal) && member.Length > 2 ? member[..^2] : member;
            var name = "the" + stem;
            var count = _names[name] = _names.GetValueOrDefault(name) + 1;
            var variable = count == 1 ? name : name + count;

            _ids[value] = (variable, type);
            _lines.Add(context.Primitive(type) == "Guid"
                ? $"var {variable} = {wrap(type, "Guid.CreateVersion7()")}; // {quote(value)} in the model"
                : $"var {variable} = {wrap(type, quote(value))};");
        }

        /// <returns>Whether any event was arranged.</returns>
        private bool given()
        {
            var groups = new List<(string? Stream, string Key, List<PartialSpec> Events)>();
            var views = new List<EmlangRef>();

            foreach (var reference in test.Given)
            {
                switch (reference.Kind)
                {
                    case EmlangElementKind.Event:
                        var stream = streamOf(reference);
                        var key = keyOf(stream, reference);
                        var group = groups.FirstOrDefault(x => x.Stream == stream && x.Key == key);
                        if (group.Events is null)
                        {
                            group = (stream, key, []);
                            groups.Add(group);
                        }

                        group.Events.Add(partial(reference));
                        break;

                    case EmlangElementKind.View:
                        // Stored directly: GivenReadModel bypasses the projection, as the model does
                        views.Add(reference);
                        break;
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var (stream, key, events) = groups[i];
                if (i == 0) _actKey = key;
                if (stream is not null) _streamKeys.TryAdd(stream, key);

                // No declared aggregate: a stream with no aggregate type (bobcat#439)
                if (stream is null)
                {
                    call($"await GivenEvents({key}, ", events, ");");
                    continue;
                }

                var verb = i == 0 ? "GivenEvents" : "GivenEventsOn";
                call($"await {verb}<{stream}>({key}, ", events, ");");
            }

            foreach (var view in views)
            {
                var typeName = document(view.Label);
                var open = $"await GivenReadModel<{typeName}>(";
                _lines.Add(open + documentPartial(typeName, view).Render(Body + open.Length) + ");");
            }

            if (groups.Count == 0 && test.When.Any(x => x.Kind == EmlangElementKind.Command))
            {
                // The act runs against a stream that starts empty: name it, from the first event expected
                var expected = test.Then.FirstOrDefault(x => x.Kind == EmlangElementKind.Event && streamName(x) is not null);
                if (expected is not null && streamName(expected) is { } stream
                    && (identityOf(stream, expected) ?? _streams.GetValueOrDefault(stream)) is { } key)
                {
                    context.Streams.Add(stream);
                    if (!carriesIdentity(stream, key))
                    {
                        // bobcat#453: the handler mints the new stream's id, so no value declared here can
                        // reach it. The id is read back from the act instead, after it runs.
                        _startedKey = (stream, key);
                    }
                    else
                    {
                        _actKey = key;
                        _lines.Add($"await GivenNoEventsFor<{stream}>({key});");
                    }
                }
            }

            if (_lines.Count > 0 && _lines[^1].Length > 0) _lines.Add("");
            return groups.Count > 0;
        }

        private void act()
        {
            var commands = test.When.Where(x => x.Kind == EmlangElementKind.Command).ToList();
            const string open = "await WhenReceived(";
            foreach (var command in commands) _lines.Add(open + addressed(command).Render(Body + open.Length) + ");");
            if (commands.Count > 0) _lines.Add("");
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
            foreach (var command in test.When.Where(x => x.Kind == EmlangElementKind.Command))
            {
                var typeName = TypeName(command.Label);
                if (CSharpModelWriter.GetsDefaultId(context.Model, typeName)) return true;
                if (context.IdentitiesOf(typeName).Count > 0) return true;
                if (CSharpModelWriter.FieldsOf(context.Model, typeName).Any(f => f.Name is "Id" or "AggregateId" || f.Name == stream + "Id")) return true;
                if (command.Props.Values.Any(x => _ids.TryGetValue(x, out var id) && id.Variable == key)) return true;
            }

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

        /// <summary>
        /// The command's partial, addressed to the arranged stream by its conventional <c>Id</c> when
        /// the model gives the command no identity of its own (bobcat#438). Without it the command's
        /// Guids were all random, so it could never reach the stream the assertion reads.
        /// </summary>
        private PartialSpec addressed(EmlangRef command)
        {
            var built = partial(command);
            var typeName = built.TypeName;
            var owner = context.Model.Slices.FirstOrDefault(s => CSharpModelWriter.Identifiers.Sanitize(s.Command ?? "") == typeName);

            // Several streams: each addressed by its own {Aggregate}Id (bobcat#444)
            var streamIds = CSharpModelWriter.StreamIdFieldsOf(context.Model, typeName);
            if (streamIds.Count > 0 && owner is not null)
            {
                foreach (var aggregate in owner.Aggregates)
                {
                    var member = CSharpModelWriter.Identifiers.Sanitize(aggregate) + "Id";
                    if (!_streamKeys.TryGetValue(TypeName(aggregate), out var key)) continue;
                    if (built.Has(member)) continue;

                    context.Member(typeName, member);
                    built.AddVariable(member, key);
                }

                return built;
            }

            if (_actKey is null || built.Has("Id")) return built;
            if (!CSharpModelWriter.GetsDefaultId(context.Model, typeName)) return built;

            // A string-keyed stream's command carries a string Id (bobcat#453), and a strong-typed one
            // carries the identity type (bobcat#512) — both of which Member would otherwise type Guid
            if (CSharpModelWriter.DefaultIdType(context.Model, typeName, context.Ids) is var idType && idType != "Guid")
            {
                if (!context.ExtraFields.TryGetValue(typeName, out var extra)) context.ExtraFields[typeName] = extra = [];
                if (extra.All(x => x.Name != "Id")) extra.Add(new CSharpModelWriter.StubField("Id", idType));
            }

            // The stream the command decides against, when the givens arranged more than one
            var actKey = owner?.Aggregates.Count == 1 && _streamKeys.TryGetValue(TypeName(owner.Aggregates[0]), out var own)
                ? own
                : _actKey;

            context.Member(typeName, "id");
            built.AddVariable("Id", actKey);
            return built;
        }

        private void assert(bool arranged)
        {
            readStartedStreamFromAct();
            var acted = test.When.Any(x => x.Kind == EmlangElementKind.Command);
            var events = test.Then.Where(x => x.Kind == EmlangElementKind.Event).ToList();
            var refusals = test.Then.Where(x => x.Kind == EmlangElementKind.Error).ToList();
            var views = test.Then.Where(x => x.Kind == EmlangElementKind.View).ToList();

            foreach (var refusal in refusals)
            {
                // A refusal's props are the values its message names: EmailAlreadyInUse { email }
                var named = refusal.Props.Values
                    .Where(x => x.Length > 0)
                    .Select(x => _ids.TryGetValue(x, out var id) ? id.Variable : quote(x));
                _lines.Add($"ThenRefusedWith({string.Join(", ", new[] { quote(refusal.Label) }.Concat(named))});");
            }

            // A command deciding against several arranged streams: each stream's events checked on it by its
            // own key, and a stream the act leaves alone said so (bobcat#444, wolverine#4865)
            var owner = context.Model.Slices.FirstOrDefault(s =>
                test.When.Any(w => w.Kind == EmlangElementKind.Command && CSharpModelWriter.Identifiers.Sanitize(s.Command ?? "") == partial(w).TypeName));
            var decidedAgainst = owner?.Aggregates.Select(TypeName).Where(_streamKeys.ContainsKey).ToList() ?? [];

            if (events.Count > 0 && decidedAgainst.Count > 1)
            {
                foreach (var stream in decidedAgainst)
                {
                    var on = events.Where(x => streamName(x) == stream).Select(partial).ToList();
                    if (on.Count == 0) _lines.Add($"ThenNoEventsOn<{stream}>({_streamKeys[stream]});");
                    else call($"ThenEventsOn<{stream}>({_streamKeys[stream]}, ", on, ");");
                }

                // An event the model puts on no stream the command decides against is still checked
                var elsewhere = events.Where(x => streamName(x) is not { } stream || !decidedAgainst.Contains(stream)).Select(partial).ToList();
                if (elsewhere.Count > 0) call("ThenEmitted(", elsewhere, ");");
            }
            else if (events.Count > 0 && !arranged && _actKey is null
                     && events.Select(streamName).Distinct().ToList() is [{ } started])
            {
                // The act starts the stream, with an id its handler assigns: one paragraph, "a VolunteerApplication
                // stream is started" and "VolunteerApplicationSubmitted is emitted on it" (wolverine#4865)
                // Asynchronous -- it reads the stream back -- so awaited, or the spec ends before it checks
                call($"await ThenStreamIsStartedWithEvents<{started}>(", events.Select(partial).ToList(), ");");
            }
            else if (events.Count > 0)
            {
                call("ThenEvents(", events.Select(partial).ToList(), ");");
            }
            else if (acted && views.Count == 0)
            {
                // A refusal appends nothing, and neither does an act the model expects nothing from
                _lines.Add("ThenNoEvents();");
            }

            foreach (var view in views)
            {
                var typeName = document(view.Label);

                // No identity in the example: a singleton view, the only one of its type
                var key = documentKey(typeName, view);
                if (key is null)
                {
                    context.Report.Add($"{where}: the {typeName} view names no identity, so it is checked as the only {typeName}.");

                    // Said in the spec too, never assumed silently (bobcat#450): a view keyed per stream
                    // needs ThenReadModel<T>(id, ...) instead
                    _lines.Add($"// The example names no {typeName} identity, so this checks the only one. If there can be");
                    _lines.Add($"// several, name the one this example means: await ThenReadModel<{typeName}>(id, ...);");
                    _lines.Add(!hasValues(view)
                        ? $"await ThenSingleReadModel<{typeName}>();"
                        : rendered($"await ThenSingleReadModel<{typeName}>(", partial(view), ");"));
                    continue;
                }

                _lines.Add(!hasValues(view)
                    ? $"await ThenReadModel<{typeName}>({key});"
                    : rendered($"await ThenReadModel<{typeName}>({key}, ", partial(view), ");"));
            }

            if (!acted && events.Count == 0 && refusals.Count == 0 && views.Count == 0)
            {
                // A test of a view that names nothing to expect: the read model does not exist
                var view = chapter.Steps.FirstOrDefault(x => x.Kind == EmlangElementKind.View);
                if (view is null)
                {
                    _lines.Add("// TODO: the model expects nothing here, and the slice has no view to look for");
                    context.Report.Add($"⚠ {where}: expects nothing, and its slice has no view.");
                    return;
                }

                var typeName = document(view.Label);
                _lines.Add(anyIdentity() is { } key
                    ? $"await ThenNoReadModel<{typeName}>({key});"
                    : $"await ThenNoReadModel<{typeName}>();");
            }
        }

        // The column a test body's statements start at: two levels in, class then method
        private const int Body = 8;

        private static string rendered(string open, PartialSpec argument, string close)
            => open + argument.Render(Body + open.Length) + close;

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

        private PartialSpec partial(EmlangRef reference)
        {
            var typeName = TypeName(reference.Label);
            if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
            var spec = new PartialSpec(typeName);

            foreach (var (prop, text) in reference.Props)
            {
                var raw = reference.Values.GetValueOrDefault(prop);
                var (member, type) = context.Member(typeName, prop, raw);
                if (text.Length > 0 && _ids.TryGetValue(text, out var id) && (id.Type == type || type == "string"))
                {
                    spec.Add(member, id.Type == type ? id.Variable : $"{id.Variable}.ToString()", "{{" + id.Variable + "}}");
                    continue;
                }

                spec.Add(member, Literal(type, text, raw, context.Ids), Cell(type, text, raw, context.Ids));
            }

            foreach (var (member, variable) in _minted.GetValueOrDefault(reference) ?? [])
            {
                spec.AddVariable(member, variable);
            }

            return spec;
        }

        private string? streamOf(EmlangRef reference)
        {
            var stream = streamName(reference);
            if (stream is not null) context.Streams.Add(stream);
            return stream;
        }

        /// <summary>The stream's identity in an event: <c>{Stream}Id</c>, or else the first identity it names.</summary>
        private string? identityOf(string? stream, EmlangRef reference)
        {
            string? fallback = null;
            foreach (var (prop, text) in reference.Props)
            {
                var (member, _) = context.Member(TypeName(reference.Label), prop, reference.Values.GetValueOrDefault(prop));
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
        private string keyOf(string? stream, EmlangRef reference)
        {
            if (identityOf(stream, reference) is { } key) return key;

            var owner = stream ?? "Stream";
            if (_streams.TryGetValue(owner, out var minted)) return minted;

            var variable = mint(owner, context.Ids.IdOf(owner) ?? "Guid");
            _streams[owner] = variable;
            return variable;
        }

        /// <summary>A read model's type, recorded as a document the stubs give an id.</summary>
        private string document(string label)
        {
            var typeName = TypeName(label);
            context.Documents.Add(typeName);
            if (!context.Elements.Contains(typeName)) context.Elements.Add(typeName);
            return typeName;
        }

        /// <summary>The identity a view's example names, or else the test's first; null when there is none.</summary>
        private string? documentKey(string typeName, EmlangRef view)
        {
            foreach (var (prop, text) in view.Props)
            {
                var (member, _) = context.Member(typeName, prop, view.Values.GetValueOrDefault(prop));
                if (member.EndsWith("Id", StringComparison.Ordinal) && _ids.TryGetValue(text, out var id)) return id.Variable;
            }

            return anyIdentity();
        }

        /// <summary>The test's first identity, which is usually the stream the view is projected from.</summary>
        private string? anyIdentity()
            => _ids.Values.FirstOrDefault(x => context.Primitive(x.Type) == "Guid").Variable;

        /// <summary>
        /// A view arranged directly: its example as a partial, with the document's <c>Id</c> set to the
        /// identity it is keyed by, so the specs that read it back find it.
        /// </summary>
        private PartialSpec documentPartial(string typeName, EmlangRef view)
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
    /// expression a <c>.With(...)</c> chain writes and the cell text a table writes.
    /// </summary>
    private sealed class PartialSpec(string typeName)
    {
        // Cell is null where the value has no faithful table form, which keeps the whole object a chain
        private readonly List<(string Member, string Expression, string? Cell)> _members = [];

        public string TypeName => typeName;

        public bool Has(string member) => _members.Any(x => x.Member == member);

        public void Add(string member, string expression, string? cell) => _members.Add((member, expression, cell));

        /// <summary>A member set to a local, which a table writes as an interpolation.</summary>
        public void AddVariable(string member, string variable, bool first = false)
        {
            var entry = (member, variable, "{{" + variable + "}}");
            if (first) _members.Insert(0, entry);
            else _members.Add(entry);
        }

        /// <param name="column">The column the partial starts at, so a chain that would run past the margin wraps.</param>
        public string Render(int column = 0)
        {
            var oneLine = $"Specify<{typeName}>()" + string.Concat(_members.Select(x => $".With(x => x.{x.Member}, {x.Expression})"));

            // A chain of two or more that fits stays on one line; a longer one, or a value with no
            // faithful cell form past the table threshold, puts each member on its own line
            var wraps = _members.Count >= 2 && column + oneLine.Length + 2 > MaxLineLength;
            if (_members.Count <= MaxWithCalls && !wraps) return oneLine;

            if (_members.Count <= MaxWithCalls || _members.Any(x => x.Cell is null))
            {
                return $"Specify<{typeName}>()" + string.Concat(_members.Select(x => $"\n    .With(x => x.{x.Member}, {x.Expression})"));
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
    /// value is written as code instead — which is what a null answer here means. (<c>CellValues</c> can
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
                var items = raw is List<object> list
                    ? list.Select(EventModelInternals.Text)
                    : trimmed.Length == 0 ? [] : [trimmed];
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
