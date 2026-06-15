using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using Wolverine.Polecat.Publishing;

namespace Wolverine.Polecat.Codegen;

internal class AncillaryOutboxFactoryFrame : SyncFrame
{
    private readonly Type _storeType;
    private readonly Type _factoryType;
    private Variable _outerFactory = null!;

    public AncillaryOutboxFactoryFrame(Type storeType)
    {
        if (!storeType.CanBeCastTo<IDocumentStore>())
        {
            throw new ArgumentOutOfRangeException(nameof(storeType), "Must be an IDocumentStore type");
        }

        _storeType = storeType;
        _factoryType = typeof(OutboxedSessionFactory<>).MakeGenericType(storeType);
    }

    public Variable? Factory { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _outerFactory = chain.FindVariable(_factoryType);
        yield return _outerFactory;

        // See Wolverine.Marten's mirror of this frame: CastVariable snapshots parent.Usage
        // at construction time, which breaks under Lamar's post-FindVariables IsOnlyOne
        // rename. Plain Variable + cast emitted in GenerateCode reads the live parent.Usage.
        Factory = new Variable(typeof(OutboxedSessionFactory), this);
        creates.Add(Factory);
        yield return Factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Factory!.Usage} = ({typeof(OutboxedSessionFactory).FullNameInCode()}){_outerFactory.Usage};");
        Next?.GenerateCode(method, writer);
    }
}
