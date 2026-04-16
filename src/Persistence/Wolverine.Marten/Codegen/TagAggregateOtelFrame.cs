using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Marten.Codegen;

/// <summary>
/// Frame that generates code to tag the current OpenTelemetry activity with
/// the aggregate stream ID and aggregate type when processing an aggregate handler workflow.
/// </summary>
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
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamId}\", {_aggregateId.Usage}.ToString());");
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.StreamType}\", \"{_aggregateType.FullName}\");");
        Next?.GenerateCode(method, writer);
    }
}
