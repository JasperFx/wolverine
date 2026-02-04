using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Frame that generates code to extract the saga ID from a saga object
/// and set it on the MessageContext for cascading message correlation.
/// </summary>
internal class SetSagaIdFromSagaFrame : SyncFrame
{
    private readonly Type _messageType;
    private readonly MemberInfo _sagaIdMember;
    private Variable? _context;
    private Variable _message;

    public SetSagaIdFromSagaFrame(Type messageType, MemberInfo sagaIdMember)
    {
        _messageType = messageType;
        _sagaIdMember = sagaIdMember;
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
        Next?.GenerateCode(method, writer);
    }
}
