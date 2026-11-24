using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// Frame that generates code to tag the current OpenTelemetry activity with
/// the aggregate stream ID and aggregate type when processing an aggregate handler workflow.
/// </summary>
/// <remarks>
/// GH-3907: was duplicated as <c>Wolverine.Marten.Codegen.TagAggregateOtelFrame</c> and its Polecat
/// twin. Nothing in it was ever store-specific — the tag names are Wolverine's own — so it moves here
/// whole. The Marten copy is the one taken: Polecat's had dropped the F# rendering.
/// </remarks>
internal class TagAggregateOtelFrame : SyncFrame
{
    private readonly Type _aggregateType;
    private readonly Variable _aggregateId;
    private readonly string _tag;
    private MessageActivityVariable? _activity;

    public TagAggregateOtelFrame(Type aggregateType, Variable aggregateId)
        : this(aggregateType, aggregateId, WolverineTracing.StreamId)
    {
    }

    private TagAggregateOtelFrame(Type aggregateType, Variable aggregateId, string tag)
    {
        _aggregateType = aggregateType;
        _aggregateId = aggregateId;
        _tag = tag;
        uses.Add(aggregateId);
    }

    /// <summary>
    /// GH-5947: a load by natural key tags the key as <see cref="WolverineTracing.StreamNaturalKey" /> ahead of the
    /// load. <see cref="WolverineTracing.StreamId" /> is the stream's real id, set by
    /// <see cref="TagLoadedStreamIdOtelFrame" /> once the aggregate is loaded.
    /// </summary>
    public static TagAggregateOtelFrame ForNaturalKey(Type aggregateType, Variable naturalKey)
        => new(aggregateType, naturalKey, WolverineTracing.StreamNaturalKey);

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // GH-5310: the message's own span, not Activity.Current -- which is marten.connection by the
        // time the stream has been fetched when Marten's TrackConnections is on.
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var activity = MessageActivityVariable.UsageFor(_activity);
        writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{_tag}\", {_aggregateId.Usage});");
        writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\");");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# has no null-conditional operator; guard the activity explicitly. SetTag returns the
        // Activity for chaining, so pipe the result to ignore. A natural key can be null, so box it
        // rather than calling ToString() on it.
        var activity = MessageActivityVariable.FSharpUsageFor(_activity);
        var value = _tag == WolverineTracing.StreamId ? $"{_aggregateId.FSharpUsage}.ToString()" : $"box {_aggregateId.FSharpUsage}";
        writer.WriteLine(
            $"if not (isNull {activity}) then {activity}.{nameof(Activity.SetTag)}(\"{_tag}\", {value}) |> ignore");
        writer.WriteLine(
            $"if not (isNull {activity}) then {activity}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\") |> ignore");
        Next?.GenerateFSharpCode(method, writer);
    }
}

/// <summary>
/// GH-5947. After a load by natural key, tags the current OpenTelemetry activity with the stream's real id, read off
/// the loaded <c>IEventStream&lt;T&gt;</c>, when an aggregate was found. Before this the natural key itself went
/// into <see cref="WolverineTracing.StreamId" />.
/// </summary>
internal class TagLoadedStreamIdOtelFrame : SyncFrame
{
    private readonly Variable _stream;
    private readonly bool _isGuidIdentity;
    private readonly bool _canBeNull;
    private MessageActivityVariable? _activity;

    public TagLoadedStreamIdOtelFrame(Variable stream, Type aggregateType, StreamIdentity identity)
    {
        _stream = stream;
        _isGuidIdentity = identity == StreamIdentity.AsGuid;
        _canBeNull = !aggregateType.IsValueType;
        uses.Add(stream);
    }

    private string member => _isGuidIdentity ? nameof(IEventStream<string>.Id) : nameof(IEventStream<string>.Key);

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // An unknown key loads no aggregate, and has no stream to name
        var activity = MessageActivityVariable.UsageFor(_activity);
        var tag = $"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_stream.Usage}.{member});";
        writer.WriteLine(_canBeNull ? $"if ({_stream.Usage}.{nameof(IEventStream<string>.Aggregate)} != null) {tag}" : tag);
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var activity = MessageActivityVariable.FSharpUsageFor(_activity);
        var found = _canBeNull ? $" && not (isNull (box {_stream.FSharpUsage}.{nameof(IEventStream<string>.Aggregate)}))" : "";
        writer.WriteLine(
            $"if not (isNull {activity}){found} then {activity}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_stream.FSharpUsage}.{member}.ToString()) |> ignore");
        Next?.GenerateFSharpCode(method, writer);
    }
}
