using System;
using Baseline.Reflection;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

internal class CreateMissingSagaFrame : SyncFrame
{
    private readonly Variable _saga;

    public CreateMissingSagaFrame(Variable saga)
    {
        if (!saga.VariableType.HasDefaultConstructor())
        {
            throw new ArgumentOutOfRangeException(nameof(saga),
                $"For now, Wolverine requires that Saga types have a public, no-arg default constructor. Missing on {saga.VariableType.FullNameInCode()}");
        }

        uses.Add(saga);
        _saga = saga;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_saga.Usage} = new {_saga.VariableType.FullNameInCode()}();");
        Next?.GenerateCode(method, writer);
    }
}
