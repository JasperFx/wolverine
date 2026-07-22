using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Wolverine.Marten.Publishing;

namespace Wolverine.Marten.Codegen;

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

        // A plain Variable assigned via a cast emitted in GenerateCode below — not a
        // CastVariable. CastVariable snapshots parent.Usage at construction time, but
        // Lamar's InjectedServiceField renames itself to the short form after FindVariables
        // when IsOnlyOne is detected. That left CastVariable.Usage holding the pre-rename
        // (Lamar-internal "_of_<TypeArg>") name, producing CS0103 references to a field
        // the host never declared. Emitting the cast at code-emit time always reads the
        // current parent.Usage.
        Factory = new Variable(typeof(OutboxedSessionFactory), this);
        creates.Add(Factory);
        yield return Factory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Factory!.Usage} = ({typeof(OutboxedSessionFactory).FullNameInCode()}){_outerFactory.Usage};");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"let {Factory!.Usage} = {_outerFactory.Usage} :?> {typeof(OutboxedSessionFactory).FullNameInCode()}");
        Next?.GenerateFSharpCode(method, writer);
    }
}