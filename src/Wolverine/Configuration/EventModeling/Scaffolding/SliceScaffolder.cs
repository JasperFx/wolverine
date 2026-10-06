using System.Diagnostics.CodeAnalysis;
using System.Text;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Wolverine.Configuration.EventModeling.Scaffolding;

/// <summary>What happened to one slice, aggregate or view when the model was scaffolded (GH-4832).</summary>
public enum ScaffoldNoticeKind
{
    /// <summary>A new file was planned (or written).</summary>
    Wrote,

    /// <summary>The file already exists, so it was left exactly as it is.</summary>
    Exists,

    /// <summary>
    ///     An existing type has to be edited by hand — an aggregate or view stub that already exists is
    ///     missing <c>Apply</c> methods. The scaffold never writes into a file it did not create.
    /// </summary>
    Edit,

    /// <summary>Nothing in the declaration or the code says how the slice is triggered.</summary>
    UnknownTrigger,

    /// <summary>The slice is a shape the scaffold does not write.</summary>
    Skipped
}

/// <summary>One line of the scaffold report.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Subject">The slice, aggregate or view it is about.</param>
/// <param name="Message">What happened, and — for <see cref="ScaffoldNoticeKind.Edit" /> — exactly what to add.</param>
/// <param name="Path">The file written, the file that exists, or the file to edit. Null when that file could not be found.</param>
public sealed record ScaffoldNotice(ScaffoldNoticeKind Kind, string Subject, string Message, string? Path = null)
{
    public override string ToString()
    {
        var label = Kind switch
        {
            ScaffoldNoticeKind.Wrote => "WROTE",
            ScaffoldNoticeKind.Exists => "EXISTS",
            ScaffoldNoticeKind.Edit => "EDIT",
            ScaffoldNoticeKind.UnknownTrigger => "UNKNOWN",
            _ => "SKIPPED"
        };

        return Path is null ? $"{label} {Subject}: {Message}" : $"{label} {Path} -- {Subject}: {Message}";
    }
}

/// <summary>A file the scaffold plans to write.</summary>
/// <param name="RelativePath">Path relative to the output directory.</param>
/// <param name="Code">The file's contents.</param>
public sealed record ScaffoldFile(string RelativePath, string Code);

/// <summary>The planned files and the report.</summary>
public sealed record ScaffoldPlan(IReadOnlyList<ScaffoldFile> Files, IReadOnlyList<ScaffoldNotice> Notices);

/// <summary>Everything <see cref="SliceScaffolder" /> needs to know about the world outside the model.</summary>
public sealed class SliceScaffoldOptions
{
    /// <summary>Namespace of the scaffolded code; a slice with a domain goes in <c>{RootNamespace}.{Domain}</c>.</summary>
    public string RootNamespace { get; set; } = "App";

    /// <summary>The CLR type behind a descriptor, when it exists. A null answer means "write a stub for it".</summary>
    public Func<TypeDescriptor, Type?> ResolveType { get; set; } = _ => null;

    /// <summary>Does a file already exist at this path, relative to the output directory?</summary>
    public Func<string, bool> FileExists { get; set; } = _ => false;

    /// <summary>The source file declaring an existing type, so an <see cref="ScaffoldNoticeKind.Edit" /> can say where.</summary>
    public Func<Type, string?> FindSourceFile { get; set; } = _ => null;
}

/// <summary>
///     GH-4832. Writes implementation skeletons for the <em>declared-only</em> slices of an Event Model —
///     slices with a <see cref="EventModelProvenance.Declared" /> claim and no <see cref="EventModelProvenance.Derived" />
///     one, which only a running host can tell apart, because no code for them exists yet.
/// </summary>
/// <remarks>
///     <para>
///         <b>Purely through <see cref="ISourceWriter" />.</b> Every file is rendered by JasperFx's
///         <see cref="SourceWriter" />, the same writer Wolverine's runtime code generation uses.
///     </para>
///     <para>
///         <b>Always Wolverine's store-agnostic style</b> (decided 2026-10-05 on jasperfx#962):
///         <c>[WriteModel]</c>, <c>Storage.StartStream</c>, <c>[Entity]</c>, <c>IStorageAction&lt;T&gt;</c> and
///         <see cref="Persistence.EventSourcing.EventsToAppend" /> with <c>[Emits]</c> — never a store-specific
///         attribute — so the scaffolded code runs unchanged on the in-memory prototyping store and on Marten,
///         Polecat or Fisher.
///     </para>
///     <para>
///         <b>Never overwrites a file.</b> An implemented slice is not declared-only, so re-running is a
///         no-op; a planned file that already exists is reported and left alone; and an aggregate or view
///         stub that already exists is never rewritten — the report says which <c>Apply</c> methods to add
///         and in which file.
///     </para>
///     <para>
///         <b>What a declaration says, not a heuristic.</b> A stream is started because the slice declares
///         <c>.StartsStream&lt;T&gt;()</c>; an aggregate is a non-nullable <c>[WriteModel]</c> because the
///         slice declares <c>.Against&lt;T&gt;()</c>; and a slice whose trigger neither the declaration nor
///         the code reveals is reported as <see cref="ScaffoldNoticeKind.UnknownTrigger" /> rather than
///         guessed. Guard reasons live in the specifications and are not scaffolded.
///     </para>
///     <para>
///         <b>A hole in the behaviour, never in the syntax.</b> Each method body says the shape to fill in
///         and throws, so one unfilled slice never stops the rest of the application from compiling — the
///         rule Bobcat's scaffolder learned (bobcat#226) before it was retired in favour of this one.
///     </para>
/// </remarks>
public static class SliceScaffolder
{
    /// <summary>Is this slice declared, with nothing derived from code behind it yet?</summary>
    public static bool IsDeclaredOnly(EventModelSliceDescriptor slice)
    {
        var declared = false;
        foreach (var role in Enum.GetValues<EventModelRole>())
        {
            if (slice.ProvenanceFor(role) is not { } rung) continue;
            if (rung > EventModelProvenance.Declared) return false;
            declared = true;
        }

        return declared;
    }

    /// <summary>Plan the skeletons for every declared-only slice of <paramref name="model" />.</summary>
    public static ScaffoldPlan Plan(EventModelDescriptor model, SliceScaffoldOptions options)
    {
        var context = new ScaffoldContext(model, options);

        foreach (var slice in model.Slices.Where(IsDeclaredOnly).OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            context.Scaffold(slice);
        }

        context.ScaffoldStateTypes();

        return new ScaffoldPlan(context.Files, context.Notices);
    }

    /// <summary>PascalCase identifier for a slice or domain name: <c>OrderPlaced (Billing)</c> → <c>OrderPlacedBilling</c>.</summary>
    public static string IdentifierFor(string name)
    {
        var builder = new StringBuilder();
        foreach (var word in name.Split(c => !char.IsLetterOrDigit(c)))
        {
            if (word.Length == 0) continue;
            builder.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        var identifier = builder.ToString();
        return identifier.Length == 0 || char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    /// <summary>The route a scaffolded HTTP endpoint is given: <c>ConfirmAppointment</c> → <c>/api/confirm-appointment</c>.</summary>
    public static string RouteFor(string identifier)
    {
        var builder = new StringBuilder("/api/");
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(identifier[i - 1])) builder.Append('-');
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static string[] Split(this string text, Func<char, bool> separator)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (separator(c))
            {
                words.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        words.Add(current.ToString());
        return words.ToArray();
    }

    private sealed class ScaffoldContext
    {
        private readonly SliceScaffoldOptions _options;

        // Name-only types are stubbed once, in the first file that needs them
        private readonly HashSet<string> _stubbed = new(StringComparer.Ordinal);

        // Aggregates and views the scaffolded slices need Apply methods on, with the events to apply
        private readonly Dictionary<string, StateType> _stateTypes = new(StringComparer.Ordinal);

        // Aggregates and views get a class of their own, so they are never stubbed as a record
        private readonly HashSet<string> _stateTypeNames = new(StringComparer.Ordinal);

        public ScaffoldContext(EventModelDescriptor model, SliceScaffoldOptions options)
        {
            _options = options;

            foreach (var slice in model.Slices.Where(IsDeclaredOnly))
            {
                foreach (var aggregate in slice.AggregateTypes) _stateTypeNames.Add(aggregate.Name);
                if (slice.StartsStream is { } stream) _stateTypeNames.Add(stream.Name);
                if (slice.Pattern == SlicePattern.View)
                {
                    foreach (var view in slice.ReadModelTypes) _stateTypeNames.Add(view.Name);
                }
            }
        }

        public List<ScaffoldFile> Files { get; } = new();
        public List<ScaffoldNotice> Notices { get; } = new();

        public void Scaffold(EventModelSliceDescriptor slice)
        {
            var pattern = slice.Pattern ?? (slice.ConsumedEvents.Count > 0 ? SlicePattern.Automation
                : slice.CommandType is not null ? SlicePattern.Command : (SlicePattern?)null);

            switch (pattern)
            {
                case SlicePattern.View:
                    scaffoldView(slice);
                    break;

                case SlicePattern.Automation:
                    scaffoldAutomation(slice);
                    break;

                case SlicePattern.Command:
                    scaffoldCommand(slice);
                    break;

                default:
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                        pattern is null
                            ? "the slice declares no pattern, command or trigger, so there is nothing to scaffold. Declare it with Command<T>(), Automation(...).On<T>() or View<T>()."
                            : $"{pattern} slices are not scaffolded."));
                    break;
            }
        }

        private void scaffoldCommand(EventModelSliceDescriptor slice)
        {
            if (slice.CommandType is null)
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                    "the Command slice declares no command type. Declare it with .Command<T>() or .Command(\"Name\")."));
                return;
            }

            switch (slice.TriggerKind)
            {
                case TriggerKind.Http:
                    writeSlice(slice, slice.CommandType, http: true);
                    break;

                case TriggerKind.MessageHandler:
                case TriggerKind.JobScheduler:
                    writeSlice(slice, slice.CommandType, http: false);
                    break;

                case TriggerKind.Grpc:
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                        $"gRPC services are not scaffolded. Write the RPC by hand; it forwards {slice.CommandType.Name} to the message bus."));
                    break;

                case null:
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.UnknownTrigger, slice.Name,
                        $"trigger Unknown -- no HTTP endpoint, message handler or gRPC service handles {slice.CommandType.Name} yet, and the declaration does not say which it will be. Declare .TriggeredBy(TriggerKind.Http) or .TriggeredBy(TriggerKind.MessageHandler) and run the scaffold again."));
                    break;

                default:
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                        $"{slice.TriggerKind} triggers are not scaffolded."));
                    break;
            }
        }

        private void scaffoldAutomation(EventModelSliceDescriptor slice)
        {
            // An automation reacts to one event, or -- Automation<TCommand>() -- is fired by the job scheduler
            var trigger = slice.ConsumedEvents.Count == 1 ? slice.ConsumedEvents[0]
                : slice.ConsumedEvents.Count == 0 ? slice.CommandType : null;

            if (trigger is null)
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.UnknownTrigger, slice.Name,
                    slice.ConsumedEvents.Count > 1
                        ? $"the automation reacts to {slice.ConsumedEvents.Count} events ({string.Join(", ", slice.ConsumedEvents.Select(x => x.Name))}), and a handler handles one. Split it into one automation per event."
                        : "trigger Unknown -- the automation declares no event it reacts to. Declare it with .On<T>()."));
                return;
            }

            writeSlice(slice, trigger, http: false);
        }

        private void scaffoldView(EventModelSliceDescriptor slice)
        {
            if (slice.ReadModelTypes.Count == 0)
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                    "the View slice declares no read model. Declare it with View<T>() or .Produces<T>()."));
                return;
            }

            foreach (var view in slice.ReadModelTypes)
            {
                stateType(view, slice.Domain, isView: true).Events.AddRange(slice.ConsumedEvents);
            }

            if (slice.ConsumedEvents.Count == 0)
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                    "the View slice declares no events it folds, so its view gets no Apply methods. Declare them with .From<T>()."));
            }
        }

        private StateType stateType(TypeDescriptor type, string? domain, bool isView)
        {
            var key = type.Name;
            if (!_stateTypes.TryGetValue(key, out var state))
            {
                _stateTypes[key] = state = new StateType(type, domain, isView);
            }

            return state;
        }

        private void writeSlice(EventModelSliceDescriptor slice, TypeDescriptor trigger, bool http)
        {
            var identifier = IdentifierFor(slice.Name);
            var path = pathFor(slice.Domain, identifier);

            if (_options.FileExists(path))
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Exists, slice.Name,
                    "the file already exists and was left exactly as it is.", path));
                return;
            }

            // The events this slice appends belong to the aggregates it works against
            foreach (var aggregate in slice.AggregateTypes)
            {
                stateType(aggregate, slice.Domain, isView: false).Events.AddRange(slice.EmittedEvents);
            }

            if (slice.StartsStream is { } started)
            {
                stateType(started, slice.Domain, isView: false).Events.AddRange(slice.EmittedEvents);
            }

            var file = new SliceFile(this, namespaceFor(slice.Domain));
            var writer = file.Body;

            // A stream the slice starts has no aggregate to load yet, so it is never a [WriteModel]
            var aggregates = slice.AggregateTypes
                .Where(x => slice.StartsStream is null || !EventModelSliceDescriptor.SameType(x, slice.StartsStream))
                .ToList();
            var aggregateType = aggregates.FirstOrDefault();
            var triggerName = file.Use(trigger);
            var triggerArgument = argumentFor(triggerName);

            var parameters = new List<string> { $"{triggerName} {triggerArgument}" };
            var shape = new List<string>();

            string? aggregateArgument = null;
            if (aggregateType is not null)
            {
                file.Namespaces.Add("Wolverine.Persistence.EventSourcing");
                var name = file.Use(aggregateType);
                aggregateArgument = argumentFor(name);
                parameters.Add($"[WriteModel] {name} {aggregateArgument}");
            }

            foreach (var extra in aggregates.Skip(1))
            {
                shape.Add($"// TODO: the slice also works against {file.Use(extra)}; load it with [ReadModel] or split the slice");
            }

            foreach (var read in slice.ReadsFrom)
            {
                file.Namespaces.Add("Wolverine.Persistence");
                var name = file.Use(read);
                parameters.Add($"[Entity] {name} {argumentFor(name)}");
            }

            // What the handler returns, in the store-agnostic vocabulary
            var returns = new List<string>();
            var emitted = slice.EmittedEvents.Select(x => file.Use(x)).ToList();
            if (emitted.Count > 0) file.Namespaces.Add("Wolverine.Persistence.EventSourcing"); // [Emits]

            if (slice.StartsStream is { } stream)
            {
                file.Namespaces.Add("Wolverine.Persistence");
                returns.Add("StartStream");
                var events = emitted.Count == 0 ? "/* the events that start it */" : string.Join(", ", emitted.Select(x => $"new {x}(...)"));
                shape.Add($"return Storage.StartStream<{file.Use(stream)}>(/* the new stream's id */, {events});");
            }
            else if (emitted.Count > 0)
            {
                file.Namespaces.Add("Wolverine.Persistence.EventSourcing");
                returns.Add("EventsToAppend");
                if (aggregateType is null)
                {
                    shape.Add("// TODO: nothing declares the stream these events go to -- declare .Against<T>() or .StartsStream<T>()");
                }

                shape.Add($"return new EventsToAppend {{ {string.Join(", ", emitted.Select(x => $"new {x}(...)"))} }};");
            }

            var outgoing = slice.PublishedMessages.ToList();
            if (slice.CommandType is { } command && !EventModelSliceDescriptor.SameType(command, trigger))
            {
                // an automation issues its command
                outgoing.Insert(0, command);
            }

            if (outgoing.Count > 0)
            {
                returns.Add("OutgoingMessages");
                shape.Add($"return new OutgoingMessages {{ {string.Join(", ", outgoing.Select(x => $"new {file.Use(x)}(...)"))} }};");
            }

            foreach (var produced in slice.ReadModelTypes)
            {
                file.Namespaces.Add("Wolverine.Persistence");
                var name = file.Use(produced);
                returns.Add($"IStorageAction<{name}>");
                shape.Add($"return Storage.Insert(new {name}(...));");
            }

            var returnType = returns.Count switch
            {
                0 => "void",
                1 => returns[0],
                _ => $"({string.Join(", ", returns)})"
            };

            var className = identifier + (http ? "Endpoint" : "Handler");

            writer.WriteLine($"// Scaffolded by `wolverine scaffold` from the declared Event Model slice '{slice.Name}'.");
            writer.WriteLine("// It is yours now: the scaffold never writes to this file again.");
            writer.Write($"BLOCK:public static class {className}");

            if (http)
            {
                file.Namespaces.Add("Microsoft.AspNetCore.Mvc");
                file.Namespaces.Add("Wolverine.Http");

                var validateParameters = aggregateArgument is null
                    ? $"{triggerName} {triggerArgument}"
                    : $"{triggerName} {triggerArgument}, {file.Use(aggregateType!)} {aggregateArgument}";

                writer.Write($"BLOCK:public static ProblemDetails Validate({validateParameters})");
                writer.WriteLine("// TODO: the guards your specifications describe. Return a ProblemDetails to refuse the request.");
                writer.WriteLine("return WolverineContinue.NoProblems;");
                writer.FinishBlock();
                writer.BlankLine();
            }

            foreach (var e in emitted)
            {
                writer.WriteLine($"[Emits(typeof({e}))]");
            }

            if (http)
            {
                writer.WriteLine($"[WolverinePost(\"{RouteFor(identifier)}\")]");
                if (returns.Count > 0) writer.WriteLine("[EmptyResponse]");
            }

            writer.Write($"BLOCK:public static {returnType} {(http ? "Post" : "Handle")}({string.Join(", ", parameters)})");
            writer.WriteLine("// Fill this in and delete the throw -- the shape is:");
            foreach (var line in shape)
            {
                writer.WriteLine(line.StartsWith("//") ? line : $"//     {line}");
            }

            writer.WriteLine($"throw new NotImplementedException(\"TODO: {slice.Name}\");");
            writer.FinishBlock();
            writer.FinishBlock();

            add(slice.Name, path, file);
        }

        /// <summary>Apply methods for every aggregate and view the scaffolded slices need them on.</summary>
        public void ScaffoldStateTypes()
        {
            foreach (var state in _stateTypes.Values.OrderBy(x => x.Type.Name, StringComparer.Ordinal))
            {
                var events = state.Events
                    .GroupBy(x => x.Name, StringComparer.Ordinal).Select(x => x.First())
                    .ToList();

                var kind = state.IsView ? "view" : "aggregate";
                var existing = _options.ResolveType(state.Type);

                if (existing is not null)
                {
                    var applied = EventModelRoles.AppliedEventsOf(existing);
                    var missing = events
                        .Where(e => !applied.Any(a => EventModelSliceDescriptor.SameType(a, e)))
                        .ToList();

                    if (missing.Count == 0) continue;

                    // The stub already exists, so it is the user's file, not ours. Say exactly what to add
                    // and where, so whoever (or whatever) picks this up can make the edit without guessing.
                    var signatures = string.Join("; ", missing.Select(e => $"public void Apply({e.Name} e)"));
                    var path = _options.FindSourceFile(existing);
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Edit, $"{existing.FullName} ({kind})",
                        $"the {kind} already exists, so it was not rewritten. Add these methods to it by hand: {signatures}" +
                        (path is null ? $". Its source file was not found; search for the declaration of {existing.Name}." : "."),
                        path));
                    continue;
                }

                var identifier = IdentifierFor(state.Type.Name);
                var filePath = pathFor(state.Domain, identifier);
                if (_options.FileExists(filePath))
                {
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Exists, $"{state.Type.Name} ({kind})",
                        "the file already exists and was left exactly as it is.", filePath));
                    continue;
                }

                // A name-only aggregate or view: ours to write in full
                var file = new SliceFile(this, namespaceFor(state.Domain));
                var writer = file.Body;

                writer.WriteLine($"// Scaffolded by `wolverine scaffold` for the declared {kind} '{state.Type.Name}'.");
                writer.Write($"BLOCK:public class {identifier}");
                writer.WriteLine("public Guid Id { get; set; }");

                foreach (var e in events)
                {
                    var name = file.Use(e);
                    writer.BlankLine();
                    writer.Write($"BLOCK:public void Apply({name} e)");
                    writer.WriteLine($"// TODO: fold {name} into the {kind}");
                    writer.FinishBlock();
                }

                writer.FinishBlock();
                add($"{state.Type.Name} ({kind})", filePath, file);
            }
        }

        private void add(string subject, string path, SliceFile file)
        {
            Files.Add(new ScaffoldFile(path, file.Render()));
            Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Wrote, subject, "scaffolded.", path));
        }

        private string namespaceFor(string? domain)
            => domain is null ? _options.RootNamespace : $"{_options.RootNamespace}.{IdentifierFor(domain)}";

        private static string pathFor(string? domain, string identifier)
            => domain is null ? $"{identifier}.cs" : Path.Combine(IdentifierFor(domain), $"{identifier}.cs");

        private static string argumentFor(string typeName)
        {
            var name = typeName.Split('<')[0].Split('.').Last();
            var argument = char.ToLowerInvariant(name[0]) + name[1..];
            return SyntaxFacts.IsKeyword(argument) ? "@" + argument : argument;
        }

        /// <summary>One file: its usings, the stub records for name-only types it is first to need, and its body.</summary>
        private sealed class SliceFile
        {
            private readonly ScaffoldContext _context;
            private readonly string _namespace;
            private readonly List<string> _stubs = new();

            public SliceFile(ScaffoldContext context, string @namespace)
            {
                _context = context;
                _namespace = @namespace;
            }

            public SortedSet<string> Namespaces { get; } = new(StringComparer.Ordinal) { "System", "Wolverine" };

            public SourceWriter Body { get; } = new();

            /// <summary>The name to write for <paramref name="type" />, stubbing it when no such type exists yet.</summary>
            [UnconditionalSuppressMessage("Trimming", "IL2072",
                Justification = "CLI scaffold path, run against a built-but-not-started host; never dispatch.")]
            public string Use(TypeDescriptor type)
            {
                if (_context._options.ResolveType(type) is { } real)
                {
                    if (real.Namespace is { } ns && ns != _namespace) Namespaces.Add(ns);
                    return real.ShortNameInCode();
                }

                if (!_context._stateTypeNames.Contains(type.Name) && _context._stubbed.Add(type.Name)) _stubs.Add(type.Name);
                return type.Name;
            }

            public string Render()
            {
                var writer = new SourceWriter();
                foreach (var ns in Namespaces) writer.UsingNamespace(ns);
                writer.BlankLine();
                writer.WriteLine($"namespace {_namespace};");
                writer.BlankLine();

                foreach (var stub in _stubs)
                {
                    writer.WriteLine("// TODO: declared by name only -- give it its fields");
                    writer.WriteLine($"public record {stub};");
                    writer.BlankLine();
                }

                foreach (var line in Body.Code().TrimEnd().Split('\n'))
                {
                    var text = line.TrimEnd('\r');
                    if (text.Length == 0) writer.BlankLine();
                    else writer.WriteLine(text);
                }

                return writer.Code();
            }
        }

        private sealed record StateType(TypeDescriptor Type, string? Domain, bool IsView)
        {
            public List<TypeDescriptor> Events { get; } = new();
        }
    }

    // Just the C# keywords a lower-cased type name can collide with
    private static class SyntaxFacts
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "event", "object", "string", "class", "record", "operator", "params", "base", "this", "checked",
            "fixed", "lock", "out", "ref", "default", "delegate", "namespace", "new", "return", "struct"
        };

        public static bool IsKeyword(string text) => Keywords.Contains(text);
    }
}
