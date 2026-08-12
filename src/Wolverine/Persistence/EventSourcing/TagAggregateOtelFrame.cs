using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
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

    public TagAggregateOtelFrame(Type aggregateType, Variable aggregateId)
    {
        _aggregateType = aggregateType;
        _aggregateId = aggregateId;
        uses.Add(aggregateId);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_aggregateId.Usage});");
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\");");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# has no null-conditional operator; guard Activity.Current explicitly. SetTag returns the
        // Activity for chaining, so pipe the result to ignore.
        writer.WriteLine(
            $"if not (isNull {typeof(Activity).FSharpName()}.{nameof(Activity.Current)}) then {typeof(Activity).FSharpName()}.{nameof(Activity.Current)}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_aggregateId.FSharpUsage}.ToString()) |> ignore");
        writer.WriteLine(
            $"if not (isNull {typeof(Activity).FSharpName()}.{nameof(Activity.Current)}) then {typeof(Activity).FSharpName()}.{nameof(Activity.Current)}.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\") |> ignore");
        Next?.GenerateFSharpCode(method, writer);
    }
}
