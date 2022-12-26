using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class ConditionalSagaInsertFrame : Frame
{
    private readonly Frame _commit;
    private readonly Frame _insert;
    private readonly Variable _saga;

    public ConditionalSagaInsertFrame(Variable saga, Frame insert, Frame commit) : base(
        insert.IsAsync || commit.IsAsync)
    {
        _saga = saga;
        _insert = insert;
        _commit = commit;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if (!{_saga.Usage}.{nameof(Saga.IsCompleted)}())");
        _insert.GenerateCode(method, writer);
        _commit.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _saga;
        foreach (var variable in _insert.FindVariables(chain)) yield return variable;

        foreach (var variable in _commit.FindVariables(chain)) yield return variable;
    }
}