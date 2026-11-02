using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
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
    private MessageActivityVariable? _activity;

    public TagAggregateOtelFrame(Type aggregateType, Variable aggregateId)
    {
        _aggregateType = aggregateType;
        _aggregateId = aggregateId;
        uses.Add(aggregateId);
    }

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
        writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_aggregateId.Usage});");
        writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\");");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# has no null-conditional operator; guard the activity explicitly. SetTag returns the
        // Activity for chaining, so pipe the result to ignore.
        var activity = MessageActivityVariable.FSharpUsageFor(_activity);
        writer.WriteLine(
            $"if not (isNull {activity}) then {activity}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_aggregateId.FSharpUsage}.ToString()) |> ignore");
        writer.WriteLine(
            $"if not (isNull {activity}) then {activity}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\") |> ignore");
        Next?.GenerateFSharpCode(method, writer);
    }
}
