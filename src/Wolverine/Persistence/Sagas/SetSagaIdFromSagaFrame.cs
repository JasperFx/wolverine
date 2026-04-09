using System.Reflection;
using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Frame that generates code to extract the saga ID from a saga object
/// and set it on the MessageContext for cascading message correlation.
/// Also tags the current OpenTelemetry activity with the saga ID and type.
/// </summary>
internal class SetSagaIdFromSagaFrame : SyncFrame
{
    private readonly Type _messageType;
    private readonly MemberInfo _sagaIdMember;
    private readonly Type? _sagaType;
    private Variable? _context;
    private Variable _message = null!;

    public SetSagaIdFromSagaFrame(Type messageType, MemberInfo sagaIdMember, Type? sagaType = null)
    {
        _messageType = messageType;
        _sagaIdMember = sagaIdMember;
        _sagaType = sagaType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _message = chain.FindVariable(_messageType);
        yield return _message;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({_message.Usage}.{_sagaIdMember.Name});");
        writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaId}\", {_message.Usage}.{_sagaIdMember.Name}.ToString());");
        if (_sagaType != null)
        {
            writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaType}\", \"{_sagaType.FullName}\");");
        }
        Next?.GenerateCode(method, writer);
    }
}
