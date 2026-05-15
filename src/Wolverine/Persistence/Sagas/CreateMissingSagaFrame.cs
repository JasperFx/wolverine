using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

internal class CreateMissingSagaFrame : SyncFrame
{
    private readonly Variable _saga;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Variable.VariableType returns the saga's runtime Type without DAM annotation. The saga type is application-rooted (registered as a handler) so its public default constructor survives trimming in any practical Wolverine setup; AOT consumers preserve saga types per the AOT publishing guide.")]
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
        writer.WriteComment($"Create a new instance of the saga state");
        writer.Write($"{_saga.Usage} = new {_saga.VariableType.FullNameInCode()}();");
        Next?.GenerateCode(method, writer);
    }
}