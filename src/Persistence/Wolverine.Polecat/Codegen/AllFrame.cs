using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using Polecat.Batching;
using Polecat.Linq;

namespace Wolverine.Polecat.Codegen;

/// <summary>
///     Emits <c>await session.Query&lt;T&gt;().ToListAsync(token)</c> for a
///     <see cref="Wolverine.Persistence.AllAttribute" /> parameter.
/// </summary>
/// <remarks>
///     <para>
///         The extension class and method are referenced through <c>typeof</c> / <c>nameof</c> rather than as
///         literal strings so that a rename in Polecat breaks this build instead of shipping a codegen failure
///         that only surfaces the first time an endpoint using the attribute is compiled at runtime.
///     </para>
///     <para>
///         Implements <see cref="IBatchableFrame" />, so a handler with more than one batchable read resolves
///         them in a <b>single round trip</b> through Polecat's <see cref="IBatchedQuery" />.
///         <see cref="PolecatBatchingPolicy" /> owns that decision and deliberately leaves a lone read standalone.
///     </para>
/// </remarks>
internal class AllFrame : AsyncFrame, IBatchableFrame
{
    private readonly Type _entityType;
    private Variable? _session;
    private Variable? _cancellation;
    private Variable? _batchQuery;
    private Variable? _batchItem;

    public AllFrame(Type entityType)
    {
        _entityType = entityType;
        Result = new Variable(typeof(IReadOnlyList<>).MakeGenericType(entityType), $"all_{entityType.Name}", this);
    }

    public Variable Result { get; }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQuery = batchQuery;
        _batchItem = new Variable(typeof(Task<>).MakeGenericType(Result.VariableType), $"{Result.Usage}_BatchItem",
            this);
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchItem == null || _batchQuery == null) return;

        // batch.Query<T>().ToList() hands back Task<IReadOnlyList<T>>, exactly this frame's result type
        writer.WriteLine(
            $"var {_batchItem.Usage} = {_batchQuery.Usage}.{nameof(IBatchedQuery.Query)}<{_entityType.FullNameInCode()}>().ToList();");
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchItem != null)
        {
            writer.WriteComment($"Every {_entityType.NameInCode()}, from the batched query");
            writer.Write($"var {Result.Usage} = await {_batchItem.Usage}.ConfigureAwait(false);");
        }
        else
        {
            writer.WriteComment($"Read every {_entityType.NameInCode()} in the database");
            writer.Write($"var {Result.Usage} = await {typeof(PolecatQueryableExtensions).FullNameInCode()}.{nameof(PolecatQueryableExtensions.ToListAsync)}({_session!.Usage}.{nameof(IQuerySession.Query)}<{_entityType.FullNameInCode()}>(), {_cancellation!.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (_batchQuery != null)
        {
            yield return _batchQuery;
            yield break;
        }

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}
