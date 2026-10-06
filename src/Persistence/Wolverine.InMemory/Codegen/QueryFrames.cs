using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;

namespace Wolverine.InMemory.Codegen;

/// <summary>
/// The declarative reads -- <c>[All]</c>, <c>[FirstOrDefault]</c> and <c>[Queryable]</c> -- over the
/// session's <c>Query&lt;T&gt;()</c>, terminated through the shared <see cref="DocumentQueryableExtensions"/>.
/// </summary>
internal class QueryFrame : AsyncFrame
{
    internal enum Shape
    {
        All,
        FirstOrDefault,
        Queryable
    }

    private readonly Type _entityType;
    private readonly Shape _shape;
    private Variable? _session;
    private Variable? _cancellation;

    public QueryFrame(Type entityType, Shape shape)
    {
        _entityType = entityType;
        _shape = shape;

        Result = shape switch
        {
            Shape.All => new Variable(typeof(IReadOnlyList<>).MakeGenericType(entityType), $"all_{entityType.Name}", this),
            Shape.FirstOrDefault => new Variable(entityType, $"firstOrDefault_{entityType.Name}", this),
            _ => new Variable(typeof(IQueryable<>).MakeGenericType(entityType), $"queryable_{entityType.Name}", this)
        };
    }

    public Variable Result { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var query = $"{_session!.Usage}.{nameof(IDocumentReadOperations.Query)}<{_entityType.FullNameInCode()}>()";
        var extensions = typeof(DocumentQueryableExtensions).FullNameInCode();

        switch (_shape)
        {
            case Shape.All:
                writer.Write($"var {Result.Usage} = await {extensions}.{nameof(DocumentQueryableExtensions.ToListAsync)}({query}, {_cancellation!.Usage}).ConfigureAwait(false);");
                break;
            case Shape.FirstOrDefault:
                writer.Write($"var {Result.Usage} = await {extensions}.{nameof(DocumentQueryableExtensions.FirstOrDefaultAsync)}({query}, {_cancellation!.Usage}).ConfigureAwait(false);");
                break;
            default:
                writer.Write($"{Result.VariableType.FullNameInCode()} {Result.Usage} = {query};");
                break;
        }

        Next?.GenerateCode(method, writer);
    }
}
