using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
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
}
