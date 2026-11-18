using System.Text;
using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// GH-5670. Draws a saga's flow as a Mermaid flowchart from its <see cref="SagaDescriptor" />: each message in, the
/// saga method that handles it, and what that method sends, schedules or (for an event-sourced saga) appends; the
/// <c>NotFound</c> path for a message that finds no saga, or a completed one; and the <c>Compensate</c> methods with
/// their undo messages. Built only from the descriptor, the same metadata CritterWatch draws its saga view from, so
/// the two pictures agree.
/// </summary>
/// <remarks>
/// Only what the methods' signatures say can be drawn. A message a handler sends through <c>IMessageBus</c>, rather
/// than returning it, can't be seen, and the diagram's legend says so.
/// </remarks>
public static class SagaFlowDiagram
{
    private static readonly string[] RoleOrder = ["Start", "StartOrHandle", "Orchestrate", "NotFound"];

    /// <summary>The Mermaid source of the saga's flow, starting with <c>flowchart TD</c>.</summary>
    public static string ToMermaid(SagaDescriptor saga)
    {
        ArgumentNullException.ThrowIfNull(saga);
        return new Drawing(saga).Draw();
    }

    /// <summary>
    /// Markdown with the saga's name as a heading and its flow in a <c>```mermaid</c> block, ready for a README or the
    /// docs. GitHub, GitLab, Azure DevOps and VitePress (with a Mermaid plugin) render the block as the diagram.
    /// </summary>
    public static string ToMarkdown(SagaDescriptor saga)
    {
        ArgumentNullException.ThrowIfNull(saga);

        var builder = new StringBuilder();
        builder.Append("## ").Append(saga.StateType.Name).Append('\n').Append('\n');
        builder.Append("```mermaid").Append('\n');
        builder.Append(ToMermaid(saga));
        builder.Append("```").Append('\n');
        return builder.ToString();
    }

    private sealed class Drawing
    {
        private readonly SagaDescriptor _saga;
        private readonly StringBuilder _lines = new();
        private readonly Dictionary<string, string> _messageIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _eventIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _scheduled = new(StringComparer.Ordinal);
        private readonly List<string> _messages = [];
        private readonly List<string> _timeouts = [];
        private readonly List<string> _events = [];
        private readonly List<string> _methods = [];
        private readonly List<string> _compensations = [];
        private readonly List<string> _edges = [];
        private bool _usesRunTime;

        public Drawing(SagaDescriptor saga)
        {
            _saga = saga;
        }

        public string Draw()
        {
            var sagaName = _saga.StateType.Name;
            var ordered = _saga.Messages
                .OrderBy(x => Array.IndexOf(RoleOrder, x.Role.ToString()))
                .ThenBy(x => x.MessageType.Name, StringComparer.Ordinal)
                .ToArray();

            foreach (var role in ordered.Where(x => x.IsTimeout))
            {
                _scheduled.Add(role.MessageType.FullName);
            }

            foreach (var method in ordered.SelectMany(x => x.Methods).Concat(_saga.Compensations.Select(x => x.Method)))
            {
                foreach (var scheduled in method.Scheduled) _scheduled.Add(scheduled.FullName);
            }

            // Every message first, in the order a reader meets them, so the ids are stable
            foreach (var role in ordered) messageId(role.MessageType);

            var sagaBody = new List<string>();

            foreach (var role in ordered)
            {
                var incoming = messageId(role.MessageType);
                foreach (var method in methodsOf(role))
                {
                    var id = uniqueId($"{method.Name}_{safe(role.MessageType.Name)}");
                    sagaBody.Add($"{id}[\"{label($"{method.Name}({displayName(role.MessageType)})")}\"]");
                    _methods.Add(id);

                    _edges.Add(method.Role == SagaRole.NotFound
                        ? $"{incoming} -.->|\"no saga, or completed\"| {id}"
                        : $"{incoming} --> {id}");

                    drawOutput(id, method, sagaBody);
                }
            }

            var index = 0;
            foreach (var compensation in _saga.Compensations)
            {
                index++;
                var failed = messageId(compensation.FailedMessageType);
                var id = uniqueId($"Compensate_{safe(compensation.FailedMessageType.Name)}_{index}");
                sagaBody.Add(
                    $"{id}[\"{label($"Compensate({displayName(compensation.FailedMessageType)}, {displayName(compensation.ExceptionType)})")}\"]");
                _compensations.Add(id);
                _edges.Add($"{failed} -.->|\"fails\"| {id}");
                drawOutput(id, compensation.Method, sagaBody);
            }

            _lines.Append("flowchart TD").Append('\n');
            line($"%% The flow of {sagaName}, drawn by Wolverine from its saga metadata");

            foreach (var message in _messageIds)
            {
                var id = message.Value;
                var name = label(displayNameOf(message.Key));
                if (_scheduled.Contains(message.Key))
                {
                    line($"{id}{{{{\"{name}\"}}}}");
                    _timeouts.Add(id);
                }
                else
                {
                    line($"{id}([\"{name}\"])");
                    _messages.Add(id);
                }
            }

            line($"subgraph {uniqueId("saga_" + safe(sagaName))}[\"{label(sagaName)}\"]");
            foreach (var node in sagaBody) line("    " + node);
            line("end");

            if (_usesRunTime)
            {
                line(_saga.IsEventSourced
                    ? "runtime[\"more messages or events, decided at run time\"]"
                    : "runtime[\"more messages, decided at run time\"]");
            }

            foreach (var edge in _edges) line(edge);

            drawLegend();

            line("classDef message fill:#e8f1fb,stroke:#3b78c2,color:#10243e");
            line("classDef timeout fill:#fff4dd,stroke:#c98a00,color:#3d2a00");
            line("classDef event fill:#ffe2c6,stroke:#d9731a,color:#3d1f00");
            line("classDef method fill:#f4f4f4,stroke:#6b6b6b,color:#1c1c1c");
            line("classDef compensate fill:#fde8e8,stroke:#c53030,color:#3d0d0d");
            line("classDef legend fill:#ffffff,stroke:#b0b0b0,color:#333333");
            classes("message", _messages);
            classes("timeout", _timeouts);
            classes("event", _events);
            classes("method", _methods);
            classes("compensate", _compensations);
            line("class legend_text legend");

            return _lines.ToString();
        }

        private IEnumerable<SagaMethodDescriptor> methodsOf(SagaMessageRole role)
        {
            // A descriptor from a service older than GH-5670 has no methods: draw the role and what it publishes
            if (role.Methods.Length > 0) return role.Methods;

            return
            [
                new SagaMethodDescriptor(role.Role switch
                {
                    SagaRole.Start => "Start",
                    SagaRole.StartOrHandle => "StartOrHandle",
                    SagaRole.NotFound => "NotFound",
                    _ => "Handle"
                }, role.Role, role.PublishedTypes.Where(x => x.FullName != _saga.StateType.FullName).ToArray(), [], [])
            ];
        }

        private void drawOutput(string from, SagaMethodDescriptor method, List<string> sagaBody)
        {
            foreach (var message in method.Messages) _edges.Add($"{from} --> {messageId(message)}");
            foreach (var message in method.Scheduled) _edges.Add($"{from} -.->|\"schedules\"| {messageId(message)}");

            foreach (var appended in method.Events)
            {
                if (!_eventIds.TryGetValue(appended.FullName, out var id))
                {
                    id = uniqueId("event_" + safe(appended.Name));
                    _eventIds[appended.FullName] = id;
                    sagaBody.Add($"{id}[/\"{label(displayName(appended))}\"/]");
                    _events.Add(id);
                }

                _edges.Add($"{from} -->|\"appends\"| {id}");
            }

            if (method.ReturnsMoreAtRunTime)
            {
                _usesRunTime = true;
                _edges.Add($"{from} -.-> runtime");
            }
        }

        private void drawLegend()
        {
            var entries = new List<string>
            {
                "Rounded: a message",
                "Hexagon: a message scheduled for later, such as a timeout",
                "Rectangle: a method of the saga"
            };

            if (_saga.IsEventSourced) entries.Add("Slanted: an event appended to the saga's own stream");

            entries.Add("Dashed: scheduled, a failure, or a message that finds no saga or a completed one");
            if (_saga.Messages.Any(x => x.IsTimeout)) entries.Add("A timeout that finds no saga, or a completed one, is dropped");
            entries.Add("Messages a method sends through IMessageBus, rather than returns, are not shown");

            line("subgraph legend[\"Legend\"]");
            line($"    legend_text[\"{string.Join("<br/>", entries.Select(label))}\"]");
            line("end");
        }

        private string messageId(TypeDescriptor type)
        {
            if (_messageIds.TryGetValue(type.FullName, out var id)) return id;

            id = uniqueId("msg_" + safe(type.Name));
            _messageIds[type.FullName] = id;
            _displayNames[type.FullName] = displayName(type);
            return id;
        }

        private readonly Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);

        private string displayNameOf(string fullName) => _displayNames[fullName];

        private string uniqueId(string candidate)
        {
            var id = candidate;
            var number = 2;
            while (!_usedIds.Add(id))
            {
                id = $"{candidate}_{number++}";
            }

            return id;
        }

        private void classes(string className, List<string> ids)
        {
            if (ids.Count > 0) line($"class {string.Join(",", ids)} {className}");
        }

        private void line(string text) => _lines.Append("    ").Append(text).Append('\n');
    }

    // Fault`1 in a type descriptor reads as Fault<ReserveStock>, taken from the full name
    private static string displayName(TypeDescriptor type)
    {
        var tick = type.Name.IndexOf('`');
        if (tick < 0) return type.Name;

        var outer = type.Name[..tick];
        var start = type.FullName.IndexOf("[[", StringComparison.Ordinal);
        if (start < 0) return outer;

        var arguments = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        for (var i = start + 1; i < type.FullName.Length; i++)
        {
            var c = type.FullName[i];
            if (c == '[')
            {
                depth++;
                if (depth == 1)
                {
                    current.Clear();
                    continue;
                }
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    arguments.Add(shortTypeName(current.ToString()));
                    continue;
                }

                if (depth < 0) break;
            }

            if (depth >= 1) current.Append(c);
        }

        return arguments.Count == 0 ? outer : $"{outer}<{string.Join(", ", arguments)}>";
    }

    // "Ns.Outer+ReserveStock, Assembly, Version=..." reads as ReserveStock
    private static string shortTypeName(string assemblyQualified)
    {
        var name = assemblyQualified.Split(',')[0].Trim();
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
        name = cut >= 0 ? name[(cut + 1)..] : name;
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    private static string safe(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }

    // Mermaid reads quotes and angle brackets in a label as its own syntax or HTML, so they go in as entity codes
    private static string label(string text)
        => text.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");
}
