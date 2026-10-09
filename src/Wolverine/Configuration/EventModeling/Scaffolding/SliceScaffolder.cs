using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
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
    Skipped,

    /// <summary>
    ///     The slice was written, but the model is missing something its code needs -- an aggregate the
    ///     command decides against, or the member that identifies one of its streams (GH-4895). The
    ///     handler says what to declare in a TODO, and the report says it here.
    /// </summary>
    Warning
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
            ScaffoldNoticeKind.Warning => "⚠ WARN",
            _ => "SKIPPED"
        };

        return Path is null ? $"{label} {Subject}: {Message}" : $"{label} {Path} -- {Subject}: {Message}";
    }
}

/// <summary>A file the scaffold plans to write, or a class it plans to append to an existing file.</summary>
/// <param name="RelativePath">
///     Path relative to the output directory -- or, when <see cref="AppendClass" /> is set, the existing
///     source file exactly as <see cref="SliceScaffoldOptions.FindSourceFile" /> reported it.
/// </param>
/// <param name="Code">The file's contents, or the class (and any stubs it needs) to append.</param>
public sealed record ScaffoldFile(string RelativePath, string Code)
{
    /// <summary>
    ///     GH-4891. Set when <see cref="Code" /> is a handler class to append to an existing file -- the
    ///     one declaring the slice's command -- rather than a new file. Names the class, so a second run
    ///     never appends it twice.
    /// </summary>
    public string? AppendClass { get; init; }

    /// <summary>
    ///     GH-4898. Set when <see cref="Code" /> is members -- the <c>Apply</c> methods an existing aggregate or
    ///     view is missing -- to insert into the class of this name in an existing file, rather than a new file
    ///     or a class appended after the others.
    /// </summary>
    public string? InsertInto { get; init; }

    /// <summary>The namespaces the appended code needs; added to the file's usings when missing.</summary>
    public IReadOnlyList<string> Usings { get; init; } = Array.Empty<string>();

    /// <summary>
    ///     The namespace the appended code belongs in, used only when the file has no file-scoped
    ///     namespace for it to land in.
    /// </summary>
    public string? Namespace { get; init; }
}

/// <summary>The planned files and the report.</summary>
public sealed record ScaffoldPlan(IReadOnlyList<ScaffoldFile> Files, IReadOnlyList<ScaffoldNotice> Notices);

/// <summary>Everything <see cref="SliceScaffolder" /> needs to know about the world outside the model.</summary>
public sealed class SliceScaffoldOptions
{
    /// <summary>
    ///     Namespace of the scaffolded code. A slice with a domain or a chapter goes in
    ///     <c>{RootNamespace}.{Domain}.{Chapter}</c>, in the matching folders (GH-4891).
    /// </summary>
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
///         <c>[WriteModel]</c>, <c>Storage.StartStream</c>, <c>Storage.AppendEvents</c>, <c>[Entity]</c>,
///         <c>IStorageAction&lt;T&gt;</c> and <see cref="Persistence.EventSourcing.EventsToAppend" /> — never a
///         store-specific attribute or <c>MartenOps</c>/<c>PolecatOps</c>/<c>FisherOps</c> — so the scaffolded
///         code runs unchanged on the in-memory prototyping store and on Marten, Polecat or Fisher.
///     </para>
///     <para>
///         <b>The signature says what it emits wherever it can</b> (GH-4889): one event onto the stream the
///         slice loads is a <c>TEvent?</c> return. Where the return type erases the events —
///         <c>StartStream</c>, <c>AppendEvents</c>, <c>EventsToAppend</c>, <c>IEventStream&lt;T&gt;</c> — the
///         source generator reads them from the body instead (GH-4914, jasperfx#990), so no shape gets
///         <c>[Emits]</c>. A new stream's id is a version 7 Guid (GH-4888).
///     </para>
///     <para>
///         <b>Every command decides against something</b> (GH-4895). One <c>.Against&lt;T&gt;()</c> is a
///         <c>[WriteModel] T</c>; two or more are one <c>[WriteModel] IEventStream&lt;T&gt;</c> each, every
///         stream identified by its own <c>{Aggregate}Id</c> member of the command. A stream with no
///         aggregate type (GH-4892) is only for a slice that purely starts one; any other slice whose model
///         names no aggregate gets a TODO and a <see cref="ScaffoldNoticeKind.Warning" />, never an append to
///         an untyped stream.
///     </para>
///     <para>
///         <b>One file per slice</b> (GH-4891): a handler is appended to the file that declares its command
///         when that file exists, and otherwise written in a folder (and namespace) per domain and chapter.
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

    /// <summary>
    ///     GH-4891. Append a planned handler class to the existing source file that declares its command:
    ///     the missing usings go after the file's own, and the class at the end of the file -- inside a
    ///     namespace block of its own when the file does not use a file-scoped namespace. Returns null,
    ///     changing nothing, when the file already declares the class.
    /// </summary>
    /// <summary>
    ///     GH-4898. Insert planned members -- the missing <c>Apply</c> methods -- at the end of the existing class
    ///     <see cref="ScaffoldFile.InsertInto" /> names, adding the usings they need. Returns null, changing
    ///     nothing, when the class is not there or has no body to insert into (a positional record), so the
    ///     caller says what to add by hand instead.
    /// </summary>
    public static string? InsertInto(string existing, ScaffoldFile file)
    {
        if (file.InsertInto is null) throw new ArgumentException("The file is not a planned insert.", nameof(file));

        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var text = existing.Replace("\r\n", "\n");

        var declaration = Regex.Match(text, $@"\b(class|record|struct)\s+{Regex.Escape(file.InsertInto)}\b");
        if (!declaration.Success) return null;

        var lineStart = text.LastIndexOf('\n', declaration.Index) + 1;
        var indent = new string(text.Skip(lineStart).TakeWhile(char.IsWhiteSpace).ToArray());
        var members = string.Join("\n", file.Code.Replace("\r\n", "\n").TrimEnd().Split('\n')
            .Select(x => x.Length == 0 ? x : indent + "    " + x));

        // The body's opening brace. A positional record with no body ends in ';' first: it gets one
        var open = -1;
        for (var i = declaration.Index; i < text.Length; i++)
        {
            if (text[i] == ';')
            {
                if (declaration.Groups[1].Value != "record") return null;
                return withUsings(text[..i].TrimEnd() + "\n" + indent + "{\n" + members + "\n" + indent + "}" + text[(i + 1)..], file, newline);
            }

            if (text[i] == '{')
            {
                open = i;
                break;
            }
        }

        if (open < 0 || closingBrace(text, open) is not { } close) return null;

        // A body written on one line -- { public Guid Id { get; set; } } -- is opened up first
        var body = text[(open + 1)..close];
        if (!body.Contains('\n'))
        {
            var reflowed = text[..open].TrimEnd() + "\n" + indent + "{"
                           + (body.Trim().Length > 0 ? "\n" + indent + "    " + body.Trim() : "")
                           + "\n" + indent + "}";
            text = reflowed + text[(close + 1)..];
            open = text.IndexOf('{', declaration.Index);
            close = closingBrace(text, open)!.Value;
        }

        var before = text[..close].TrimEnd();
        var inserted = before + (before.EndsWith('{') ? "\n" : "\n\n") + members + "\n" + indent + text[close..];
        return withUsings(inserted, file, newline);
    }

    private static string withUsings(string inserted, ScaffoldFile file, string newline)
    {
        var lines = inserted.Split('\n').ToList();
        var fileScoped = lines.Select(x => Regex.Match(x, @"^\s*namespace\s+([\w.]+)\s*;")).FirstOrDefault(x => x.Success);
        var ownNamespace = fileScoped?.Groups[1].Value;
        var missing = file.Usings
            .Where(u => u != ownNamespace)
            .Where(u => !lines.Any(x => Regex.IsMatch(x, $@"^\s*using\s+{Regex.Escape(u)}\s*;")))
            .Select(u => $"using {u};")
            .ToList();

        if (missing.Count > 0)
        {
            var lastUsing = lines.FindLastIndex(x => Regex.IsMatch(x, @"^\s*using\s+[\w.]+\s*;"));
            if (lastUsing < 0) missing.Add("");
            var at = lastUsing >= 0
                ? lastUsing + 1
                : Math.Max(0, lines.FindIndex(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith("//")));
            lines.InsertRange(at, missing);
        }

        return string.Join(newline, lines);
    }

    // The '}' matching the '{' at open, skipping braces inside strings, characters and comments
    private static int? closingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i) is var end and >= 0 ? end : text.Length;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 1 : text.Length;
                continue;
            }

            if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c; i++)
                {
                    if (text[i] == '\\') i++;
                }

                continue;
            }

            if (c == '{') depth++;
            if (c == '}' && --depth == 0) return i;
        }

        return null;
    }

    public static string? AppendTo(string existing, ScaffoldFile file)
    {
        if (file.AppendClass is null) throw new ArgumentException("The file is not a planned append.", nameof(file));

        if (Regex.IsMatch(existing, $@"\bclass\s+{Regex.Escape(file.AppendClass)}\b")) return null;

        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();

        var fileScoped = lines.Select(x => Regex.Match(x, @"^\s*namespace\s+([\w.]+)\s*;")).FirstOrDefault(x => x.Success);
        var blockScoped = !fileScoped?.Success ?? lines.Any(x => Regex.IsMatch(x, @"^\s*namespace\s+[\w.]+\s*(\{|$)"));
        var ownNamespace = fileScoped?.Groups[1].Value ?? file.Namespace;

        var missing = file.Usings
            .Where(u => u != ownNamespace)
            .Where(u => !lines.Any(x => Regex.IsMatch(x, $@"^\s*using\s+{Regex.Escape(u)}\s*;")))
            .Select(u => $"using {u};")
            .ToList();

        if (missing.Count > 0)
        {
            var lastUsing = lines.FindLastIndex(x => Regex.IsMatch(x, @"^\s*using\s+[\w.]+\s*;"));
            if (lastUsing < 0) missing.Add("");
            var at = lastUsing >= 0
                ? lastUsing + 1
                : Math.Max(0, lines.FindIndex(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith("//")));
            lines.InsertRange(at, missing);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add("");

        var body = file.Code.Replace("\r\n", "\n").TrimEnd().Split('\n');
        if (blockScoped && file.Namespace is not null)
        {
            lines.Add($"namespace {file.Namespace}");
            lines.Add("{");
            lines.AddRange(body.Select(x => x.Length == 0 ? x : "    " + x));
            lines.Add("}");
        }
        else
        {
            lines.AddRange(body);
        }

        lines.Add("");
        return string.Join(newline, lines);
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

                case TriggerKind.Human:
                    // GH-4884: a screen is not a transport. Every command an import of a board produces is
                    // screen-triggered, and the specs generated beside it send the command through the
                    // message bus, so a message handler is what they can drive. Said in the report.
                    writeSlice(slice, slice.CommandType, http: false,
                        note: " Triggered by a person (TriggerKind.Human), so it is scaffolded as a message handler the UI sends to; declare .TriggeredBy(TriggerKind.Http) instead for an HTTP endpoint.");
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
                stateType(view, slice, isView: true).Events.AddRange(slice.ConsumedEvents);
            }

            if (slice.ConsumedEvents.Count == 0)
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Skipped, slice.Name,
                    "the View slice declares no events it folds, so its view gets no Apply methods. Declare them with .From<T>()."));
            }
        }

        private StateType stateType(TypeDescriptor type, EventModelSliceDescriptor slice, bool isView)
        {
            var key = type.Name;
            if (!_stateTypes.TryGetValue(key, out var state))
            {
                _stateTypes[key] = state = new StateType(type, groupsOf(slice), isView);
            }

            return state;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "CLI scaffold path; never dispatch. The command type is the application's own, loaded to write code against it.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "CLI scaffold path; never dispatch. The command type is the application's own, loaded to write code against it.")]
        private static bool commandHasMember(Type? type, string name)
            => type is not null && (type.GetProperty(name) is not null || type.GetField(name) is not null);

        private void writeSlice(EventModelSliceDescriptor slice, TypeDescriptor trigger, bool http, string? note = null)
        {
            var identifier = IdentifierFor(slice.Name);
            var className = identifier + (http ? "Endpoint" : "Handler");

            // GH-4891: the handler lives in the same file as the command it handles, when that file
            // exists -- one file per slice, the way `bobcat import-event-model` writes them
            var appendTo = appendTargetFor(slice, trigger);
            var path = appendTo ?? pathFor(slice, identifier);

            if (appendTo is null && _options.FileExists(path))
            {
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Exists, slice.Name,
                    "the file already exists and was left exactly as it is.", path));
                return;
            }

            // The events this slice appends belong to the aggregates it works against
            foreach (var aggregate in slice.AggregateTypes)
            {
                stateType(aggregate, slice, isView: false).Events.AddRange(slice.EmittedEvents);
            }

            if (slice.StartsStream is { } started)
            {
                stateType(started, slice, isView: false).Events.AddRange(slice.EmittedEvents);
            }

            var file = new SliceFile(this, appendTo is null ? namespaceFor(slice) : null);
            var writer = file.Body;

            // A stream the slice starts has no aggregate to load yet, so it is never a [WriteModel]
            var aggregates = slice.AggregateTypes
                .Where(x => slice.StartsStream is null || !EventModelSliceDescriptor.SameType(x, slice.StartsStream))
                .ToList();
            // GH-4895: two or more aggregates are one IEventStream<T> each, so there is no single
            // aggregate for the typed shapes or for Validate
            var multiStream = aggregates.Count > 1;
            var aggregateType = multiStream ? null : aggregates.FirstOrDefault();
            var triggerName = file.Use(trigger);
            // The command is just `command`, whatever its type is called; an event that triggers an
            // automation keeps its own name
            var triggerArgument = slice.CommandType is { } sliceCommand && EventModelSliceDescriptor.SameType(sliceCommand, trigger)
                ? "command"
                : argumentFor(triggerName);

            var parameters = new List<string> { $"{triggerName} {triggerArgument}" };
            var shape = new List<string>();

            string? aggregateName = null;
            string? aggregateArgument = null;
            if (aggregateType is not null)
            {
                file.Namespaces.Add("Wolverine.Persistence.EventSourcing");
                aggregateName = file.Use(aggregateType);
                aggregateArgument = argumentFor(aggregateName);
                parameters.Add($"[WriteModel] {aggregateName} {aggregateArgument}");
            }

            // GH-4895: a command that decides against several streams takes each as an IEventStream<T>,
            // found by its own {Aggregate}Id member of the command -- the same [WriteModel(name)] lookup
            // the multi-stream aggregate handler workflow already uses
            var streams = new List<(string Argument, string Event)>();
            if (multiStream)
            {
                file.Namespaces.Add("JasperFx.Events");
                file.Namespaces.Add("Wolverine.Persistence.EventSourcing");
                var commandType = _options.ResolveType(triggerTypeFor(slice, trigger));
                foreach (var aggregate in aggregates)
                {
                    var name = file.Use(aggregate);
                    var argument = argumentFor(name) + "Stream";
                    var idMember = aggregate.Name + "Id";
                    var hasMember = commandHasMember(commandType, idMember);
                    var source = hasMember ? $"nameof({triggerName}.{idMember})" : $"\"{idMember}\"";
                    if (!hasMember)
                    {
                        shape.Add($"// TODO: {triggerName} needs a {idMember} member identifying the {name} stream");
                        Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Warning, slice.Name,
                            $"the command decides against {aggregates.Count} streams, and {triggerName} has no {idMember} member to identify the {name} one. Add it to the command."));
                    }

                    parameters.Add($"[WriteModel({source})] IEventStream<{name}> {argument}");
                    streams.Add((argument, name));
                }
            }

            foreach (var read in slice.ReadsFrom)
            {
                file.Namespaces.Add("Wolverine.Persistence");
                var name = file.Use(read);
                parameters.Add($"[Entity] {name} {argumentFor(name)}");
            }

            var emitted = slice.EmittedEvents.Select(x => file.Use(x)).ToList();

            var outgoing = slice.PublishedMessages.ToList();
            if (slice.CommandType is { } command && !EventModelSliceDescriptor.SameType(command, trigger))
            {
                // an automation issues its command
                outgoing.Insert(0, command);
            }

            // GH-4889: a slice that appends at most one event to the stream it loads, and does nothing
            // else, says what it emits in its signature -- TEvent? -- so it needs no [Emits]. Returning
            // null appends nothing (GH-4309).
            var typedEvent = aggregateType is not null && !multiStream && slice.StartsStream is null && emitted.Count == 1 &&
                             outgoing.Count == 0 && slice.ReadModelTypes.Count == 0;

            // What the handler returns, in the store-agnostic vocabulary
            var returns = new List<string>();

            if (slice.StartsStream is { } stream)
            {
                // GH-4888: a sequential (version 7) Guid, never Guid.NewGuid() -- a random stream id
                // fragments the event store's indexes
                file.Namespaces.Add("Wolverine.Persistence");
                returns.Add("StartStream");
                var events = emitted.Count == 0 ? "/* the events that start it */" : string.Join(", ", emitted.Select(x => $"new {x}(...)"));
                shape.Add("var id = Guid.CreateVersion7();");
                shape.Add($"return Storage.StartStream<{file.Use(stream)}>(id, {events});");
            }
            else if (typedEvent)
            {
                returns.Add($"{emitted[0]}?");
                shape.Add($"return new {emitted[0]}(...);   // or null when there is nothing to record");
                shape.Add($"// If the stream may not exist yet, make the parameter {aggregateName}? -- [WriteModel] then does not require it");
            }
            else if (emitted.Count > 0 && aggregateType is not null)
            {
                file.Namespaces.Add("Wolverine.Persistence.EventSourcing");
                returns.Add("EventsToAppend");
                shape.Add($"return new EventsToAppend {{ {string.Join(", ", emitted.Select(x => $"new {x}(...)"))} }};");
            }
            else if (multiStream)
            {
                // Each event goes onto the stream it belongs to; the generator reads which from the body
                shape.Add("// Append each event to the stream it belongs to:");
                foreach (var e in emitted)
                {
                    shape.Add($"{streams[0].Argument}.AppendOne(new {e}(...));   // or {string.Join(" / ", streams.Skip(1).Select(x => x.Argument))}");
                }
            }
            else if (slice.AggregateDeclaration == AggregateDeclaration.DeciderModel)
            {
                // GH-4919: decides through a DCB decider model (jasperfx#994). The DCB handler shape is still
                // being designed (GH-4865), so nothing is guessed: say what was declared, and warn
                var decider = slice.DeciderModel is { } model ? file.Use(model) : "its decider model";
                shape.Add($"// TODO: this slice decides through the DCB decider model {decider}. The Dynamic");
                shape.Add("// Consistency Boundary handler shape is not designed yet (GH-4865), so write it by hand.");
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Warning, slice.Name,
                    $"the slice declares the DCB decider model {decider}, and the DCB handler shape is not designed yet (GH-4865), so its handler is a TODO."));
            }
            else if (slice.AggregateDeclaration == AggregateDeclaration.None && emitted.Count > 0)
            {
                // GH-4919: deliberately no aggregate (.NoAggregate()) -- legitimate only for a slice that purely
                // starts a stream, so the shape is a stream with no aggregate type (GH-4892), and no warning
                file.Namespaces.Add("Wolverine.Persistence");
                returns.Add("StartStream");
                shape.Add("// Declared .NoAggregate(): a stream with no aggregate type");
                shape.Add("var id = Guid.CreateVersion7();");
                shape.Add($"return Storage.StartStream(id, {string.Join(", ", emitted.Select(x => $"new {x}(...)"))});");
            }
            else if (emitted.Count > 0)
            {
                // GH-4895: the model names no aggregate. A stream with no aggregate type (GH-4892) is only
                // right for a slice that purely starts one, which the model has not said this one does --
                // so no append to an untyped stream is guessed. The handler says what to declare.
                var events = string.Join(", ", emitted.Select(x => $"new {x}(...)"));
                shape.Add("// TODO: the model names no aggregate this command decides against. Declare it on the");
                shape.Add("// slice -- .Against<T>(), once per stream, or .StartsStream<T>() when the slice starts one --");
                shape.Add("// and scaffold again. Only a slice that purely starts a stream may do without one:");
                shape.Add($"//     return Storage.StartStream(Guid.CreateVersion7(), {events});   // and return StartStream");
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Warning, slice.Name,
                    "the model names no aggregate this command decides against, so its handler is a TODO. Declare .Against<T>() (or .StartsStream<T>() if it only starts a stream) and scaffold again."));
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

            writer.WriteLine($"// Scaffolded by `wolverine scaffold` from the declared Event Model slice '{slice.Name}'.");
            writer.WriteLine("// It is yours now: the scaffold never writes to this class again.");
            writer.Write($"BLOCK:public static class {className}");

            if (http)
            {
                file.Namespaces.Add("Microsoft.AspNetCore.Mvc");
                file.Namespaces.Add("Wolverine.Http");

                var validateParameters = aggregateArgument is null
                    ? new List<string> { $"{triggerName} {triggerArgument}" }
                    : new List<string> { $"{triggerName} {triggerArgument}", $"{aggregateName} {aggregateArgument}" };

                writer.Write(signature("public static ProblemDetails Validate", validateParameters));
                writer.WriteLine("// TODO: the guards your specifications describe. Return a ProblemDetails to refuse the request.");
                writer.WriteLine("return WolverineContinue.NoProblems;");
                writer.FinishBlock();
                writer.BlankLine();
            }

            // No [Emits] (GH-4914): a typed return says it, and JasperFx.Events.SourceGenerator reads the
            // events every other shape constructs in its body into the emitted-events manifest (jasperfx#990)

            if (http)
            {
                writer.WriteLine($"[WolverinePost(\"{RouteFor(identifier)}\")]");
                if (returns.Count > 0) writer.WriteLine("[EmptyResponse]");
            }

            writer.Write(signature($"public static {returnType} {(http ? "Post" : "Handle")}", parameters));
            writer.WriteLine("// Fill this in and delete the throw -- the shape is:");
            foreach (var line in shape)
            {
                writer.WriteLine(line.StartsWith("//") ? line : $"//     {line}");
            }

            writer.WriteLine($"throw new NotImplementedException(\"TODO: {slice.Name}\");");
            writer.FinishBlock();
            writer.FinishBlock();

            if (appendTo is not null)
            {
                Files.Add(new ScaffoldFile(appendTo, file.RenderBody())
                {
                    AppendClass = className,
                    Usings = file.Namespaces.ToArray(),
                    Namespace = namespaceOf(slice, trigger)
                });
                Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Wrote, slice.Name,
                    $"{className} appended to the file that declares {triggerTypeFor(slice, trigger).Name}.{note}", appendTo));
                return;
            }

            add(slice.Name, path, file, note);
        }

        /// <summary>
        ///     The source file the slice's handler is appended to: the one declaring the slice's command
        ///     (for an automation, the command it issues), when that type exists and its file is found.
        /// </summary>
        private string? appendTargetFor(EventModelSliceDescriptor slice, TypeDescriptor trigger)
        {
            if (_options.ResolveType(triggerTypeFor(slice, trigger)) is not { } type) return null;
            return _options.FindSourceFile(type);
        }

        private static TypeDescriptor triggerTypeFor(EventModelSliceDescriptor slice, TypeDescriptor trigger)
            => slice.CommandType ?? trigger;

        private string? namespaceOf(EventModelSliceDescriptor slice, TypeDescriptor trigger)
            => _options.ResolveType(triggerTypeFor(slice, trigger))?.Namespace;

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

                    var signatures = string.Join("; ", missing.Select(e => isRecord(existing)
                        ? $"public {existing.Name} Apply({e.Name} e)"
                        : $"public void Apply({e.Name} e)"));
                    var path = _options.FindSourceFile(existing);

                    // GH-4898: the methods go INTO the existing class -- the import writes aggregates and
                    // views as bare stubs, and without these every spec against them fails to project. Only
                    // the missing ones, each a TODO; nothing the class already has is touched.
                    if (path is not null)
                    {
                        var members = new SourceWriter();
                        var usings = new SortedSet<string>(StringComparer.Ordinal);
                        var first = true;

                        // A record's members are init-only, so its Apply returns the new state -- the
                        // immutable shape the stores fold a view or aggregate with -- rather than mutating
                        var immutable = isRecord(existing);
                        foreach (var e in missing)
                        {
                            if (!first) members.BlankLine();
                            first = false;

                            var resolved = _options.ResolveType(e);
                            if (resolved?.Namespace is { } ns && ns != existing.Namespace) usings.Add(ns);
                            var name = resolved?.ShortNameInCode() ?? e.Name;

                            if (immutable)
                            {
                                members.Write($"BLOCK:public {existing.ShortNameInCode()} Apply({name} e)");
                                members.WriteLine($"// TODO: fold {name} into the {kind}, as a copy: this with {{ ... }}");
                                members.WriteLine("return this;");
                            }
                            else
                            {
                                members.Write($"BLOCK:public void Apply({name} e)");
                                members.WriteLine($"// TODO: fold {name} into the {kind}");
                            }

                            members.FinishBlock();
                        }

                        Files.Add(new ScaffoldFile(path, members.Code())
                        {
                            InsertInto = existing.Name,
                            Usings = usings.ToArray()
                        });
                        Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Wrote, $"{existing.FullName} ({kind})",
                            $"added {signatures} to the existing {kind}.", path));
                        continue;
                    }

                    // No source file to edit: say exactly what to add, so whoever picks this up need not guess
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Edit, $"{existing.FullName} ({kind})",
                        $"the {kind} already exists, so it was not rewritten. Add these methods to it by hand: {signatures}" +
                        $". Its source file was not found; search for the declaration of {existing.Name}.",
                        path));
                    continue;
                }

                var identifier = IdentifierFor(state.Type.Name);
                var filePath = pathFor(state.Groups, identifier);
                if (_options.FileExists(filePath))
                {
                    Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Exists, $"{state.Type.Name} ({kind})",
                        "the file already exists and was left exactly as it is.", filePath));
                    continue;
                }

                // A name-only aggregate or view: ours to write in full
                var file = new SliceFile(this, namespaceFor(state.Groups));
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

        // The compiler gives every record a clone method; nothing else has one
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "CLI scaffold path, run against a built-but-not-started host; never dispatch.")]
        private static bool isRecord(Type type) => type.GetMethod("<Clone>$") is not null;

        private void add(string subject, string path, SliceFile file, string? note = null)
        {
            Files.Add(new ScaffoldFile(path, file.Render()));
            Notices.Add(new ScaffoldNotice(ScaffoldNoticeKind.Wrote, subject, "scaffolded." + note, path));
        }

        // GH-4891: a folder, and a namespace, per domain and per chapter of the model
        private static string[] groupsOf(EventModelSliceDescriptor slice)
            => new[] { slice.Domain, slice.Chapter }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => IdentifierFor(x!))
                .ToArray();

        private string namespaceFor(EventModelSliceDescriptor slice) => namespaceFor(groupsOf(slice));

        private string namespaceFor(string[] groups)
            => string.Join(".", new[] { _options.RootNamespace }.Concat(groups));

        private static string pathFor(EventModelSliceDescriptor slice, string identifier)
            => pathFor(groupsOf(slice), identifier);

        private static string pathFor(string[] groups, string identifier)
            => Path.Combine(groups.Append($"{identifier}.cs").ToArray());

        // A method's opening line as a BLOCK: directive. A signature too long to read on one line puts
        // each parameter on its own line instead
        private static string signature(string method, IReadOnlyList<string> parameters)
        {
            var oneLine = $"{method}({string.Join(", ", parameters)})";
            if (parameters.Count < 2 || oneLine.Length <= MaxSignatureLength) return "BLOCK:" + oneLine;

            var lines = new List<string> { method + "(" };
            for (var i = 0; i < parameters.Count; i++)
            {
                var last = i == parameters.Count - 1;
                lines.Add((last ? "BLOCK:" : "") + "    " + parameters[i] + (last ? ")" : ","));
            }

            return string.Join("\n", lines);
        }

        // A 120-column margin, less the four columns of the method's indentation in its class
        private const int MaxSignatureLength = 116;

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
            private readonly string? _namespace;
            private readonly List<string> _stubs = new();

            // A null namespace: the code is appended into an existing file, which already has one
            public SliceFile(ScaffoldContext context, string? @namespace)
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

                writeBody(writer);
                return writer.Code();
            }

            /// <summary>The stubs and the body alone, with no usings or namespace: code to append to a file.</summary>
            public string RenderBody()
            {
                var writer = new SourceWriter();
                writeBody(writer);
                return writer.Code();
            }

            private void writeBody(SourceWriter writer)
            {
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
            }
        }

        private sealed record StateType(TypeDescriptor Type, string[] Groups, bool IsView)
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
