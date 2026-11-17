using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class ConditionalSagaInsertFrame : Frame
{
    private readonly Frame _commit;
    private readonly Frame _insert;
    private readonly Variable _saga;
    private readonly bool _retain;

    /// <param name="retain">
    ///     GH-5572. True for a saga type whose completed instances are kept: a saga that completes in its starting
    ///     message is marked completed and inserted anyway
    /// </param>
    public ConditionalSagaInsertFrame(Variable saga, Frame insert, Frame commit, bool retain = false) : base(
        insert.IsAsync || commit.IsAsync)
    {
        _saga = saga;
        _insert = insert;
        _commit = commit;
        _retain = retain;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_retain)
        {
            writer.WriteComment("Completed sagas of this type are retained, so the saga is inserted even if it already completed");
            writer.Write($"BLOCK:if ({_saga.Usage}.{nameof(Saga.IsCompleted)}())");
            writer.Write($"{typeof(SagaRetention).FullName}.{nameof(SagaRetention.MarkCompleted)}({_saga.Usage});");
            writer.FinishBlock();
            StampSagaLastModifiedFrame.WriteStamp(writer, _saga);
            _insert.GenerateCode(method, writer);
            _commit.GenerateCode(method, writer);

            Next?.GenerateCode(method, writer);
            return;
        }

        writer.Write($"BLOCK:if (!{_saga.Usage}.{nameof(Saga.IsCompleted)}())");
        // GH-5345: the persistence frame, not user code, owns Saga.LastModified
        StampSagaLastModifiedFrame.WriteStamp(writer, _saga);
        _insert.GenerateCode(method, writer);

        writer.FinishBlock();

        _commit.GenerateCode(method, writer);

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_retain)
        {
            writer.Write($"BLOCK:if {_saga.Usage}.{nameof(Saga.IsCompleted)}() then");
            writer.Write($"{typeof(SagaRetention).FullName}.{nameof(SagaRetention.MarkCompleted)}({_saga.Usage})");
            writer.FinishBlock();
            StampSagaLastModifiedFrame.WriteFSharpStamp(writer, _saga);
            _insert.GenerateFSharpCode(method, writer);
            _commit.GenerateFSharpCode(method, writer);

            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        writer.Write($"BLOCK:if not ({_saga.Usage}.{nameof(Saga.IsCompleted)}()) then");
        StampSagaLastModifiedFrame.WriteFSharpStamp(writer, _saga);
        _insert.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        _commit.GenerateFSharpCode(method, writer);

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _saga;
        foreach (var variable in _insert.FindVariables(chain)) yield return variable;

        foreach (var variable in _commit.FindVariables(chain)) yield return variable;
    }
}