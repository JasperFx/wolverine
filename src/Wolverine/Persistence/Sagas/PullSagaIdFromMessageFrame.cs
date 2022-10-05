using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Baseline;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Oakton.Parsing;

namespace Wolverine.Persistence.Sagas;

internal class PullSagaIdFromMessageFrame : SyncFrame
{
    private readonly Type _messageType;
    private readonly MemberInfo _sagaIdMember;
    private Variable? _envelope;
    private Variable? _message;
    private readonly Type? _sagaIdType;

    public PullSagaIdFromMessageFrame(Type messageType, MemberInfo sagaIdMember)
    {
        _messageType = messageType;
        _sagaIdMember = sagaIdMember;

        _sagaIdType = sagaIdMember.GetMemberType();
        if (!SagaFramePolicy.ValidSagaIdTypes.Contains(_sagaIdType))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType),
                $"SagaId must be one of {SagaFramePolicy.ValidSagaIdTypes.Select(x => x.NameInCode()).Join(", ")}, but was {_sagaIdType.NameInCode()}");
        }

        SagaId = new Variable(_sagaIdType, SagaFramePolicy.SagaIdVariableName, this);
    }

    public Variable SagaId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_sagaIdType == typeof(string))
        {
            writer.Write(
                $"{_sagaIdType.NameInCode()} {SagaFramePolicy.SagaIdVariableName} = {_envelope!.Usage}.{nameof(Envelope.SagaId)} ?? {_message!.Usage}.{_sagaIdMember.Name};");
            writer.Write(
                $"if (string.{nameof(string.IsNullOrEmpty)}({SagaFramePolicy.SagaIdVariableName})) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
        }
        else
        {
            var typeNameInCode = _sagaIdType == typeof(Guid)
                ? typeof(Guid).FullName
                : _sagaIdType.NameInCode();


            writer.Write(
                $"if (!{typeNameInCode}.TryParse({_envelope!.Usage}.{nameof(Envelope.SagaId)}, out {typeNameInCode} sagaId)) sagaId = {_message!.Usage}.{_sagaIdMember.Name};");

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

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _message = chain.FindVariable(_messageType);
        yield return _message;

        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}
