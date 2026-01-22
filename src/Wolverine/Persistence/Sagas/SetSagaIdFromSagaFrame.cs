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
    private readonly Variable _saga;
    private readonly MemberInfo _sagaIdMember;
    private Variable? _context;

    public SetSagaIdFromSagaFrame(Variable saga, MemberInfo sagaIdMember)
    {
        _saga = saga;
        _sagaIdMember = sagaIdMember;
        uses.Add(_saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var idAccess = $"{_saga.Usage}.{_sagaIdMember.Name}";
        writer.Write($"{_context!.Usage}.{nameof(MessageContext.SetSagaId)}({idAccess});");
        Next?.GenerateCode(method, writer);
    }
}
