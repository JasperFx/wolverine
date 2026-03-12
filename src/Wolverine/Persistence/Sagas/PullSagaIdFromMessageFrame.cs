using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

internal class PullSagaIdFromMessageFrame : SyncFrame
{
    private readonly Type _messageType;
    private readonly MemberInfo _sagaIdMember;
    private readonly Type? _sagaIdType;
    private readonly bool _isStrongTypedId;
    private Variable? _envelope;
    private Variable? _message;

    public PullSagaIdFromMessageFrame(Type messageType, MemberInfo sagaIdMember)
    {
        _messageType = messageType;
        _sagaIdMember = sagaIdMember;

        _sagaIdType = sagaIdMember.GetMemberType();
        if (!SagaChain.IsValidSagaIdType(_sagaIdType!))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType),
                $"SagaId must be one of {SagaChain.ValidSagaIdTypes.Select(x => x.NameInCode()).Join(", ")} or a strong-typed identifier, but was {_sagaIdType!.NameInCode()}");
        }

        _isStrongTypedId = !SagaChain.ValidSagaIdTypes.Contains(_sagaIdType);

        SagaId = new Variable(_sagaIdType!, SagaChain.SagaIdVariableName, this);
    }

    public Variable SagaId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_isStrongTypedId)
        {
            generateStrongTypedIdCode(writer);
        }
        else if (_sagaIdType == typeof(string))
        {
            writer.Write(
                $"{_sagaIdType.NameInCode()} {SagaChain.SagaIdVariableName} = {_message!.Usage}.{_sagaIdMember.Name} ?? {_envelope!.Usage}.{nameof(Envelope.SagaId)};");
            writer.Write(
                $"if (string.{nameof(string.IsNullOrEmpty)}({SagaChain.SagaIdVariableName})) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
        }
        else
        {
            var typeNameInCode = _sagaIdType == typeof(Guid)
                ? typeof(Guid).FullName
                : _sagaIdType!.NameInCode();

            writer.Write($"{typeNameInCode} {SagaChain.SagaIdVariableName} = {_message!.Usage}.{_sagaIdMember.Name};");
            writer.Write(
                $"if ({SagaChain.SagaIdVariableName} == default && !{typeNameInCode}.TryParse({_envelope!.Usage}.{nameof(Envelope.SagaId)}, out sagaId)) sagaId = {_message!.Usage}.{_sagaIdMember.Name};");

            if (_sagaIdType == typeof(Guid))
            {
                writer.Write(
                    $"if ({SagaId.Usage} == System.Guid.Empty) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
            }
            else
            {
                writer.Write(
                    $"if ({SagaId.Usage} == 0) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
            }
        }

        Next?.GenerateCode(method, writer);
    }

    private void generateStrongTypedIdCode(ISourceWriter writer)
    {
        var typeNameInCode = _sagaIdType!.FullNameInCode();

        // For strong-typed identifiers, read directly from the message property
        writer.Write(
            $"var {SagaChain.SagaIdVariableName} = {_message!.Usage}.{_sagaIdMember.Name};");

        // Check for default value
        writer.Write(
            $"if ({SagaChain.SagaIdVariableName}.Equals(default({typeNameInCode}))) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope!.Usage});");
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _message = chain.FindVariable(_messageType);
        yield return _message;

        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}
