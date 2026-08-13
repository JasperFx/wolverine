using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
///     Exposes CosmosDb's raw <c>IQueryable&lt;T&gt;</c> for a
///     <see cref="Wolverine.Persistence.QueryableAttribute" /> parameter.
/// </summary>
/// <remarks>
///     <b>Read the caveat.</b> Wolverine's CosmosDb integration writes every user document into a single
///     shared <c>wolverine</c> container -- the same one holding its own envelopes, node records and locks --
///     with no per-type discriminator on user documents. A queryable obtained here is therefore scoped to that
///     container, not to <c>T</c>, and an unfiltered query can deserialize documents of entirely other types
///     as <c>T</c>. This is the same limitation that makes <c>[FirstOrDefault]</c> and <c>[All]</c> refuse to
///     support CosmosDb at all; <c>[Queryable]</c> supports it because the whole point of the attribute is to
///     hand you the store's own API, but you own the filtering.
/// </remarks>
internal class QueryableFrame : SyncFrame
{
    private readonly Type _elementType;
    private Variable? _container;

    public QueryableFrame(Type elementType)
    {
        _elementType = elementType;
        Result = new Variable(typeof(IQueryable<>).MakeGenericType(elementType), $"queryable_{elementType.Name}",
            this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"The raw CosmosDb IQueryable for {_elementType.NameInCode()}");
        writer.WriteComment(
            "NOTE: this container is shared across every document type; filter accordingly");
        writer.Write(
            $"{Result.VariableType.FullNameInCode()} {Result.Usage} = {_container!.Usage}.{nameof(Container.GetItemLinqQueryable)}<{_elementType.FullNameInCode()}>();");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;
    }
}
