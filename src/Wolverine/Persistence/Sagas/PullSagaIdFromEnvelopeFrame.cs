using System;
using System.Collections.Generic;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class PullSagaIdFromEnvelopeFrame : SyncFrame
{
    private Variable? _envelope;

    public PullSagaIdFromEnvelopeFrame(Type sagaIdType)
    {
        SagaId = new Variable(sagaIdType, SagaChain.SagaIdVariableName, this);
    }

    public Variable SagaId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (SagaId.VariableType == typeof(string))
        {
            writer.Write($"var {SagaId.Usage} = {_envelope!.Usage}.{nameof(Envelope.SagaId)};");
            writer.Write(
                $"if (string.{nameof(string.IsNullOrEmpty)}({SagaChain.SagaIdVariableName})) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
        }
        else
        {
            var typeNameInCode = SagaId.VariableType == typeof(Guid)
                ? typeof(Guid).FullName
                : SagaId.VariableType.NameInCode();

            writer.Write(
                $"if (!{typeNameInCode}.TryParse({_envelope!.Usage}.{nameof(Envelope.SagaId)}, out {typeNameInCode} sagaId)) throw new {typeof(IndeterminateSagaStateIdException).FullName}({_envelope.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}