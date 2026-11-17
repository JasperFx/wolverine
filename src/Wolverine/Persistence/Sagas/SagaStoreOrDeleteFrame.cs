using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class SagaStoreOrDeleteFrame : Frame
{
    private readonly Frame _delete;
    private readonly Variable _saga;
    private readonly Frame _update;
    private readonly bool _retain;

    /// <param name="retain">
    ///     GH-5572. True for a saga type whose completed instances are kept: a completed saga is marked completed and
    ///     updated rather than deleted
    /// </param>
    public SagaStoreOrDeleteFrame(Variable saga, Frame update, Frame delete, bool retain = false)
        : base(update.IsAsync || delete.IsAsync)
    {
        uses.Add(saga);
        _saga = saga;
        _update = update;
        _delete = delete;
        _retain = retain;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in _update.FindVariables(chain)) yield return variable;

        foreach (var variable in _delete.FindVariables(chain)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_retain)
        {
            writer.WriteComment("Completed sagas of this type are retained: mark it completed and update it");
            writer.Write($"BLOCK:if ({_saga.Usage}.{nameof(Saga.IsCompleted)}())");
            writer.Write($"{typeof(SagaRetention).FullName}.{nameof(SagaRetention.MarkCompleted)}({_saga.Usage});");
            writer.FinishBlock();
            StampSagaLastModifiedFrame.WriteStamp(writer, _saga);
            _update.GenerateCode(method, writer);

            Next?.GenerateCode(method, writer);
            return;
        }

        writer.WriteComment("Delete the saga if completed, otherwise update it");
        writer.Write($"BLOCK:if ({_saga.Usage}.{nameof(Saga.IsCompleted)}())");
        _delete.GenerateCode(method, writer);
        writer.FinishBlock();
        writer.Write("BLOCK:else");
        // GH-5345: the persistence frame, not user code, owns Saga.LastModified
        StampSagaLastModifiedFrame.WriteStamp(writer, _saga);
        _update.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_retain)
        {
            writer.WriteComment("Completed sagas of this type are retained: mark it completed and update it");
            writer.Write($"BLOCK:if {_saga.Usage}.{nameof(Saga.IsCompleted)}() then");
            writer.Write($"{typeof(SagaRetention).FullName}.{nameof(SagaRetention.MarkCompleted)}({_saga.Usage})");
            writer.FinishBlock();
            StampSagaLastModifiedFrame.WriteFSharpStamp(writer, _saga);
            _update.GenerateFSharpCode(method, writer);

            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        writer.WriteComment("Delete the saga if completed, otherwise update it");
        writer.Write($"BLOCK:if {_saga.Usage}.{nameof(Saga.IsCompleted)}() then");
        _delete.GenerateFSharpCode(method, writer);
        writer.FinishBlock();
        writer.Write("BLOCK:else");
        StampSagaLastModifiedFrame.WriteFSharpStamp(writer, _saga);
        _update.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateFSharpCode(method, writer);
    }
}