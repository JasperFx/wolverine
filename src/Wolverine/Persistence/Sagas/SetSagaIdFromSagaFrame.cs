using System.Reflection;
using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Logging;
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
    private MessageActivityVariable? _activity;
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

        // GH-5310: the message's own span, not Activity.Current -- which a persistence integration
        // (Marten's TrackConnections) may have moved by now.
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var activity = MessageActivityVariable.UsageFor(_activity);
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({_message.Usage}.{_sagaIdMember.Name});");
        writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaId}\", {_message.Usage}.{_sagaIdMember.Name}.ToString());");
        if (_sagaType != null)
        {
            writer.WriteLine($"{activity}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaType}\", \"{_sagaType.FullName}\");");
        }
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var member = $"{_message.Usage}.{_sagaIdMember.Name}";
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({member})");

        var current = MessageActivityVariable.FSharpUsageFor(_activity);
        writer.Write($"BLOCK:if not (isNull {current}) then");
        writer.Write($"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaId}\", {member}.ToString()) |> ignore");
        if (_sagaType != null)
        {
            writer.Write($"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.SagaType}\", \"{_sagaType.FullName}\") |> ignore");
        }

        writer.FinishBlock();
        Next?.GenerateFSharpCode(method, writer);
    }
}
