using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Frame that generates code to set the saga ID on the MessageContext
/// so that cascading messages will have the correct saga ID attached.
/// Also tags the current OpenTelemetry activity with the saga ID and type.
/// </summary>
internal class SetSagaIdFrame : SyncFrame
{
    private readonly Variable _sagaId;
    private readonly Type? _sagaType;
    private Variable? _context;

    public SetSagaIdFrame(Variable sagaId, Type? sagaType = null)
    {
        _sagaId = sagaId;
        _sagaType = sagaType;
        uses.Add(_sagaId);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({_sagaId.Usage});");
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaId}\", {_sagaId.Usage}.ToString());");
        if (_sagaType != null)
        {
            writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaType}\", \"{_sagaType.FullName}\");");
        }
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var sagaId = _sagaId.FSharpUsage;
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({sagaId})");

        // F# has no null-conditional `?.`, and SetTag returns the Activity; guard once and pipe to ignore.
        var current = $"{typeof(Activity).FSharpName()}.{nameof(Activity.Current)}";
        writer.Write($"BLOCK:if not (isNull {current}) then");
        writer.Write($"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaId}\", {sagaId}.ToString()) |> ignore");
        if (_sagaType != null)
        {
            writer.Write($"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaType}\", \"{_sagaType.FullName}\") |> ignore");
        }

        writer.FinishBlock();
        Next?.GenerateFSharpCode(method, writer);
    }
}
