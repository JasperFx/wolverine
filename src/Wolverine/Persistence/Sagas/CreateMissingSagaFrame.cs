using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Sagas;

internal class CreateMissingSagaFrame : SyncFrame
{
    private readonly Variable _saga;
    private readonly IChain? _chain;

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Variable.VariableType returns the saga's runtime Type without DAM annotation. The saga type is application-rooted (registered as a handler) so its public default constructor survives trimming in any practical Wolverine setup; AOT consumers preserve saga types per the AOT publishing guide.")]
    public CreateMissingSagaFrame(Variable saga, IChain? chain = null)
    {
        if (!saga.VariableType.HasDefaultConstructor())
        {
            throw new ArgumentOutOfRangeException(nameof(saga),
                $"For now, Wolverine requires that Saga types have a public, no-arg default constructor. Missing on {saga.VariableType.FullNameInCode()}");
        }

        uses.Add(saga);
        _saga = saga;
        _chain = chain;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Create a new instance of the saga state");
        writer.Write($"{_saga.Usage} = new {_saga.VariableType.FullNameInCode()}();");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // The C# form reassigns the loaded (null) saga variable. An F# binding is immutable, and the loaded one is
        // often a `let!`, so this shadows it instead. The frame only ever runs as the first step of the saga-missing
        // branch, and everything that branch does with the saga comes after it, inside the same branch, so every
        // later reference sees the new instance just as it does in C#. The postprocessors are the exception: they are
        // written after the saga's if/else, where the shadowed (null) binding is back in scope, so refuse a chain
        // whose postprocessors read the saga rather than hand them null.
        assertNoPostprocessorReadsTheSaga();

        writer.WriteComment("Create a new instance of the saga state");
        writer.Write($"{_saga.FSharpAssignmentUsage} = {_saga.VariableType.FSharpName()}()");
        Next?.GenerateFSharpCode(method, writer);
    }

    private void assertNoPostprocessorReadsTheSaga()
    {
        if (_chain == null) return;

        var readers = _chain.Postprocessors.Concat(_chain.PostCommitPostprocessors)
            .Where(frame => frame.Uses.Contains(_saga))
            .Select(describe)
            .ToArray();

        if (readers.Length == 0) return;

        throw new NotSupportedException(
            $"F# code generation does not yet support postprocessors that read a saga created for a missing saga id. " +
            $"In F# they would receive null instead of the new {_saga.VariableType.FullNameInCode()}. " +
            $"Postprocessors reading the saga: {string.Join(", ", readers)}");
    }

    private static string describe(Frame frame) => frame is MethodCall call
        ? $"{call.HandlerType.FullNameInCode()}.{call.Method.Name}()"
        : frame.GetType().FullNameInCode();
}
