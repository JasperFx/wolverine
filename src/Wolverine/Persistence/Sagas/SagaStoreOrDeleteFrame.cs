using System.Collections.Generic;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class SagaStoreOrDeleteFrame : Frame
{
    private readonly Frame _delete;
    private readonly Variable _saga;
    private readonly Frame _update;

    public SagaStoreOrDeleteFrame(Variable saga, Frame update, Frame delete) : base(update.IsAsync || delete.IsAsync)
    {
        uses.Add(saga);
        _saga = saga;
        _update = update;
        _delete = delete;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in _update.FindVariables(chain)) yield return variable;

        foreach (var variable in _delete.FindVariables(chain)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if ({_saga.Usage}.{nameof(Saga.IsCompleted)}())");
        _delete.GenerateCode(method, writer);
        writer.FinishBlock();
        writer.Write("BLOCK:else");
        _update.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}