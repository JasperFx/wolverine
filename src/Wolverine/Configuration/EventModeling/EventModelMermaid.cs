using System.Text;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-6007: the assembled Event Model as a Mermaid <c>eventmodeling</c> diagram (Mermaid 11.15+), so the
///     picture pastes into a GitHub issue, a pull request, a wiki or a Vitepress page without a viewer. One
///     way only — the diagram is a rendering of the descriptor, never a source of it.
/// </summary>
/// <remarks>
///     <para>
///     Every slice becomes a run of numbered frames by its pattern — a Command is <c>ui</c> (when a person
///     triggers it and the trigger has a label) → <c>cmd</c> → <c>evt</c>, a View is <c>evt</c> → <c>rmo</c>, an
///     Automation is <c>evt</c> → <c>rmo</c> → <c>pcr</c> → <c>cmd</c> → <c>evt</c>, a Translation starts on a reset
///     frame from the external system's event and a Query is its trigger → <c>rmo</c>. Each slice starts on a
///     reset frame (<c>rf</c>) so one slice's last event does not flow into the next slice's trigger by
///     inference; slices connect where one consumes an event another emitted, drawn with <c>->></c> back to
///     the frame that emitted it. Lanes come from a dotted namespace on the identifier: the stream an event is
///     appended to, the aggregate a slice decides against, or failing both the slice's domain.
///     </para>
///     <para>
///     A <c>gwt</c> block per bound specification carries the specification's identity as a comment and the
///     slice's own given / when / then shape; specifications have no bodies on the descriptor yet, so every
///     block on one slice reads the same. Hotspots, provenance, chapters, published messages and external
///     system URIs have no place in the grammar and are not carried; a chapter or a domain is only a comment
///     at the top of its slice.
///     </para>
///     <para>
///     Identifiers are <c>[_a-zA-Z][\w]*</c> in the grammar, so every name is reduced to that deterministically:
///     each run of letters and digits is kept and capitalised at its start, everything else is dropped
///     (<c>"GET /api/accounts/{id}"</c> is <c>GETApiAccountsId</c>, <c>"New customer walks in"</c> is
///     <c>NewCustomerWalksIn</c>). Frame numbers are at most three digits in the grammar; a model with more
///     than 999 frames gets a comment saying the diagram will not parse.
///     </para>
/// </remarks>
public static class EventModelMermaid
{
    private const int MaxFrames = 999;

    /// <summary>Render <paramref name="model" /> as a Mermaid <c>eventmodeling</c> diagram.</summary>
    public static string Render(EventModelDescriptor model)
    {
        var writer = new Writer(model);
        return writer.Render();
    }

    /// <summary>
    ///     The grammar's identifier form of a name: runs of letters and digits kept and capitalised at their
    ///     start, everything else dropped, a leading underscore when it would otherwise start with a digit,
    ///     <c>_</c> when nothing is left.
    /// </summary>
    public static string Identifier(string? name)
    {
        if (name.IsEmpty()) return "_";

        var builder = new StringBuilder(name!.Length);
        var startOfRun = true;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                // Underscores stay where they are; the grammar allows them anywhere
                builder.Append(startOfRun && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                startOfRun = false;
            }
            else
            {
                startOfRun = true;
            }
        }

        if (builder.Length == 0) return "_";
        if (char.IsDigit(builder[0])) builder.Insert(0, '_');
        return builder.ToString();
    }

    /// <summary>A lane-qualified identifier: <c>Lane.Name</c>, or just <c>Name</c> with no lane.</summary>
    internal static string Qualified(string? lane, string name)
        => lane.IsEmpty() ? Identifier(name) : $"{Identifier(lane)}.{Identifier(name)}";

    private sealed class Writer
    {
        private readonly EventModelDescriptor _model;
        private readonly StringBuilder _text = new();
        private readonly List<string> _gwt = new();
        private readonly SortedSet<string> _entities = new(StringComparer.Ordinal);

        // The frame that emitted each event identifier, so a slice consuming it can point back
        private readonly Dictionary<string, List<int>> _eventFrames = new(StringComparer.Ordinal);

        // What lane an event lives in, from the model as a whole rather than one slice
        private readonly Dictionary<string, string> _eventLanes = new(StringComparer.Ordinal);

        private int _frame;

        public Writer(EventModelDescriptor model)
        {
            _model = model;

            // The lane an event lives in, most specific claim first: the stream a slice says it appends the
            // event to, then the aggregate that folds it, then the lane of a slice that emits it
            foreach (var stream in model.Slices.SelectMany(x => x.EmittedEventStreams))
            {
                _eventLanes.TryAdd(stream.Event.Name, stream.Aggregate.Name);
            }

            foreach (var aggregate in model.Aggregates)
            {
                foreach (var applied in aggregate.AppliedEvents) _eventLanes.TryAdd(applied.Name, aggregate.Type.Name);
            }

            foreach (var slice in model.Slices)
            {
                if (sliceLane(slice) is { } lane)
                {
                    foreach (var emitted in slice.EmittedEvents) _eventLanes.TryAdd(emitted.Name, lane);
                }
            }
        }

        public string Render()
        {
            // The model's name as YAML front matter: the grammar's own `title` line is in the Langium source
            // but Mermaid 12.4 refuses it ("Expecting token of type 'EOF' but found `title`"), while the
            // front matter every Mermaid diagram takes renders the title the same way
            _text.AppendLine("---");
            _text.AppendLine($"title: \"{_model.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            _text.AppendLine("---");
            _text.AppendLine("eventmodeling");

            // Producers before consumers, so a view or an automation can point back at the frame that emitted
            // the event it consumes rather than drawing the event again: commands and translations, then
            // automations, then views, then queries, each in the model's own order
            foreach (var slice in _model.Slices.OrderBy(order))
            {
                _text.AppendLine();
                writeSlice(slice);
            }

            if (_frame > MaxFrames)
            {
                _text.AppendLine();
                _text.AppendLine(
                    $"    %% {_frame} frames: the eventmodeling grammar numbers frames with at most three digits, so this diagram will not parse past frame {MaxFrames}");
            }

            if (_entities.Count > 0)
            {
                _text.AppendLine();
                foreach (var entity in _entities) _text.AppendLine($"    entity {entity}");
            }

            foreach (var block in _gwt)
            {
                _text.AppendLine();
                _text.Append(block);
            }

            return _text.ToString();
        }

        private void writeSlice(EventModelSliceDescriptor slice)
        {
            var heading = $"    %% {slice.Name} ({slice.Pattern?.ToString() ?? "no pattern"})";
            var where = new[] { slice.Domain, slice.Chapter }.Where(x => x.IsNotEmpty()).ToArray();
            if (where.Length > 0) heading += $" in {string.Join(" / ", where)}";
            _text.AppendLine(heading);

            switch (slice.Pattern)
            {
                case SlicePattern.View:
                    writeView(slice);
                    break;
                case SlicePattern.Automation:
                    writeAutomation(slice);
                    break;
                case SlicePattern.Translation:
                    writeTranslation(slice);
                    break;
                case SlicePattern.Query:
                    writeQuery(slice);
                    break;
                default:
                    writeCommand(slice);
                    break;
            }
        }

        // ui (a labelled human trigger) -> cmd -> evt
        private void writeCommand(EventModelSliceDescriptor slice)
        {
            var lane = sliceLane(slice);
            int? previous = null;

            if (humanLabel(slice) is { } label)
            {
                previous = frame("ui", Qualified(lane, label));
            }

            var command = slice.CommandType?.Name ?? slice.Name;
            var cmd = frame("cmd", Qualified(lane, command), previous);

            writeEmitted(slice, cmd);
            writeGwt(slice, cmd, given: givenEvents(slice), when: ("cmd", command), then: thenOf(slice));
        }

        // evt (what the view folds) -> rmo
        private void writeView(EventModelSliceDescriptor slice)
        {
            var sources = consumedEventFrames(slice);
            var readModel = readModelOf(slice);
            var rmo = frame("rmo", Qualified(sliceLane(slice), readModel), sources);

            writeGwt(slice, rmo, given: eventStatements(slice.ConsumedEvents), when: null, then: [("rmo", readModel)]);
        }

        // evt -> rmo -> pcr -> cmd -> evt
        private void writeAutomation(EventModelSliceDescriptor slice)
        {
            var lane = sliceLane(slice);
            var sources = consumedEventFrames(slice);
            var previous = sources.ToList();

            foreach (var read in slice.ReadsFrom.Concat(slice.ReadModelTypes).Select(x => x.Name).Distinct())
            {
                previous = [frame("rmo", Qualified(lane, read), previous)];
            }

            var processor = slice.HandlerType?.Name ?? slice.Name;
            var pcr = frame("pcr", Qualified(lane, processor), previous);

            var last = pcr;
            if (slice.CommandType is { } command)
            {
                last = frame("cmd", Qualified(lane, command.Name), pcr);
            }

            writeEmitted(slice, last);

            var given = eventStatements(slice.ConsumedEvents)
                .Concat(slice.ReadsFrom.Select(x => ("rmo", x.Name)))
                .ToList();
            writeGwt(slice, pcr, given, slice.CommandType is { } c ? ("cmd", c.Name) : null, thenOf(slice));
        }

        // rf evt External.Event -> pcr -> (cmd) -> evt
        private void writeTranslation(EventModelSliceDescriptor slice)
        {
            var lane = sliceLane(slice);
            var external = slice.ExternalSystems.FirstOrDefault(x => x.Direction == ExternalSystemDirection.Inbound)?.Name
                           ?? slice.ExternalSystems.FirstOrDefault()?.Name;

            var previous = new List<int>();
            foreach (var consumed in slice.ConsumedEvents)
            {
                // A translation's inbound event is the other system's, so it lives in that system's lane
                // rather than one of ours, and starts fresh rather than pointing at a frame of ours
                previous.Add(frame("evt", Qualified(external ?? laneOf(consumed, lane), consumed.Name)));
            }

            var processor = slice.HandlerType?.Name ?? slice.Name;
            var pcr = frame("pcr", Qualified(lane, processor), previous);

            var last = pcr;
            if (slice.CommandType is { } command)
            {
                last = frame("cmd", Qualified(lane, command.Name), pcr);
            }

            writeEmitted(slice, last);
            writeGwt(slice, pcr, eventStatements(slice.ConsumedEvents),
                slice.CommandType is { } c ? ("cmd", c.Name) : null, thenOf(slice));
        }

        // (ui) -> rmo: what the query reads
        private void writeQuery(EventModelSliceDescriptor slice)
        {
            var lane = sliceLane(slice);
            int? trigger = null;
            if (humanLabel(slice) is { } label)
            {
                trigger = frame("ui", Qualified(lane, label));
            }

            var reads = slice.ReadsFrom.Concat(slice.ReadModelTypes).Select(x => x.Name).Distinct().ToList();
            if (reads.Count == 0) reads.Add(readModelOf(slice));

            foreach (var read in reads)
            {
                frame("rmo", Qualified(lane, read), trigger);
            }

            // A query arranges nothing and changes nothing, so there is no given / then to write
        }

        private void writeEmitted(EventModelSliceDescriptor slice, int from)
        {
            var lane = sliceLane(slice);
            foreach (var emitted in slice.EmittedEvents)
            {
                var number = frame("evt", Qualified(slice.StreamFor(emitted)?.Name ?? laneOf(emitted, lane), emitted.Name), from);
                if (!_eventFrames.TryGetValue(Identifier(emitted.Name), out var frames))
                {
                    _eventFrames[Identifier(emitted.Name)] = frames = new List<int>();
                }

                frames.Add(number);
            }
        }

        /// <summary>
        ///     The frames to point a consuming slice back at: the frames that emitted each consumed event
        ///     earlier in the diagram, or a fresh event frame when nothing in the diagram emitted it.
        /// </summary>
        private List<int> consumedEventFrames(EventModelSliceDescriptor slice)
        {
            var sources = new List<int>();
            foreach (var consumed in slice.ConsumedEvents)
            {
                if (_eventFrames.TryGetValue(Identifier(consumed.Name), out var emitted))
                {
                    sources.AddRange(emitted);
                }
                else
                {
                    var number = frame("evt", Qualified(laneOf(consumed, sliceLane(slice)), consumed.Name));
                    _eventFrames[Identifier(consumed.Name)] = [number];
                    sources.Add(number);
                }
            }

            return sources;
        }

        /// <summary>
        ///     Write one frame. The first frame of a slice and any frame with explicit sources is a reset frame,
        ///     so nothing is related by inference that the model did not say; a frame following exactly one
        ///     frame of its own slice is a time frame and relates to it by inference.
        /// </summary>
        private int frame(string type, string identifier, IEnumerable<int>? sources = null)
        {
            var list = sources?.Distinct().OrderBy(x => x).ToList() ?? new List<int>();
            return frame(type, identifier, list.Count == 1 && list[0] == _frame ? _frame : null, list);
        }

        private int frame(string type, string identifier, int? previous)
            => frame(type, identifier, previous.HasValue ? new[] { previous.Value } : null);

        private int frame(string type, string identifier, int? inferredFrom, List<int> sources)
        {
            _frame++;
            var number = _frame;

            if (inferredFrom.HasValue)
            {
                _text.AppendLine($"    tf {number:00} {type} {identifier}");
            }
            else
            {
                var relations = string.Concat(sources.Select(x => $" ->> {x:00}"));
                _text.AppendLine($"    rf {number:00} {type} {identifier}{relations}");
            }

            return number;
        }

        private void writeGwt(EventModelSliceDescriptor slice, int frame, IReadOnlyList<(string Type, string Name)> given,
            (string Type, string Name)? when, IReadOnlyList<(string Type, string Name)> then)
        {
            if (slice.Specifications.Count == 0) return;

            // given and then are each at least one statement in the grammar
            if (given.Count == 0 || then.Count == 0)
            {
                var block = new StringBuilder();
                foreach (var specification in slice.Specifications)
                {
                    block.AppendLine($"    %% spec: {specification.Identity} (no given / then the model can state)");
                }

                _gwt.Add(block.ToString());
                return;
            }

            foreach (var statement in given.Concat(then).Append(when ?? default).Where(x => x.Name is not null))
            {
                _entities.Add(Identifier(statement.Name));
            }

            foreach (var specification in slice.Specifications)
            {
                var block = new StringBuilder();
                block.AppendLine($"    %% spec: {specification.Identity}");
                block.Append($"    gwt {frame:00} given");
                foreach (var (type, name) in given) block.Append($" {type} {Identifier(name)}");
                if (when is { } w) block.Append($" when {w.Type} {Identifier(w.Name)}");
                block.Append(" then");
                foreach (var (type, name) in then) block.Append($" {type} {Identifier(name)}");
                block.AppendLine();
                _gwt.Add(block.ToString());
            }
        }

        /// <summary>
        ///     What a command slice is given: the events its aggregate folds, failing that the events it
        ///     consumes, failing that its own events as an earlier occurrence.
        /// </summary>
        private List<(string, string)> givenEvents(EventModelSliceDescriptor slice)
        {
            var aggregates = slice.AggregateTypes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            if (slice.StartsStream is { } started) aggregates.Add(started.Name);
            if (slice.DeciderModel is { } decider) aggregates.Add(decider.Name);

            var applied = _model.Aggregates
                .Where(x => aggregates.Contains(x.Type.Name))
                .SelectMany(x => x.AppliedEvents)
                .ToList();

            if (applied.Count > 0) return eventStatements(applied);
            if (slice.ConsumedEvents.Count > 0) return eventStatements(slice.ConsumedEvents);
            return eventStatements(slice.EmittedEvents);
        }

        private static List<(string, string)> eventStatements(IEnumerable<TypeDescriptor> events)
            => events.Select(x => x.Name).Distinct().Select(x => ("evt", x)).ToList();

        private static List<(string, string)> thenOf(EventModelSliceDescriptor slice)
            => eventStatements(slice.EmittedEvents);

        private static int order(EventModelSliceDescriptor slice) => slice.Pattern switch
        {
            SlicePattern.Automation => 1,
            SlicePattern.View => 2,
            SlicePattern.Query => 3,
            _ => 0
        };

        /// <summary>
        ///     The label to draw as the slice's <c>ui</c>: a trigger label on a slice a person triggers, or on
        ///     one received in a way a person can reach (an HTTP route, a message) — the overlay labels a
        ///     route's trigger "Customer at the ATM" without restating that it is HTTP. Never on a schedule
        ///     or an external system's trigger.
        /// </summary>
        private static string? humanLabel(EventModelSliceDescriptor slice)
        {
            if (slice.TriggerLabel.IsEmpty()) return null;
            if (slice.IsTriggeredByHuman()) return slice.TriggerLabel;
            return slice.TriggerKind is null || slice.TriggerKind.Value.CanBeHumanTriggered() ? slice.TriggerLabel : null;
        }

        private static string readModelOf(EventModelSliceDescriptor slice)
            => slice.ReadModelTypes.FirstOrDefault()?.Name ?? slice.ProjectionTypes.FirstOrDefault()?.Name ?? slice.Name;

        /// <summary>The aggregate a slice works against, or failing that its domain.</summary>
        private static string? sliceLane(EventModelSliceDescriptor slice)
            => slice.AggregateTypes.FirstOrDefault()?.Name
               ?? slice.StartsStream?.Name
               ?? slice.DeciderModel?.Name
               ?? (slice.Domain.IsNotEmpty() ? slice.Domain : null);

        private string? laneOf(TypeDescriptor @event, string? fallback)
            => _eventLanes.TryGetValue(@event.Name, out var lane) ? lane : fallback;
    }
}
