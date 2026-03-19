// NOTE: This file requires Polecat 1.1+ (OutboxedSessionFactory<T>)
// Uncomment #if POLECAT_1_1 / #endif when ready, or remove the guards after upgrading the Polecat NuGet
#if POLECAT_1_1
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
    private Variable _outerFactory;

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

        Factory = new CastVariable(_outerFactory, typeof(OutboxedSessionFactory));
        creates.Add(Factory);
        yield return Factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // This only exists to resolve the variables
        Next?.GenerateCode(method, writer);
    }
}
#endif
