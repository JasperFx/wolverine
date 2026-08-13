using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;

namespace Wolverine.Marten.Codegen;

/// <summary>
///     Emits <c>await session.Query&lt;T&gt;().ToListAsync(token)</c> for a
///     <see cref="Wolverine.Persistence.AllAttribute" /> parameter.
/// </summary>
/// <remarks>
///     The extension class and method are referenced through <c>typeof</c> / <c>nameof</c> rather than as literal
///     strings so that a rename in Marten breaks this build instead of shipping a codegen failure that only
///     surfaces the first time an endpoint using the attribute is compiled at runtime.
/// </remarks>
internal class AllFrame : AsyncFrame
{
    private readonly Type _entityType;
    private Variable? _session;
    private Variable? _cancellation;

    public AllFrame(Type entityType)
    {
        _entityType = entityType;
        Result = new Variable(typeof(IReadOnlyList<>).MakeGenericType(entityType), $"all_{entityType.Name}", this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Read every {_entityType.NameInCode()} in the database");
        writer.Write($"var {Result.Usage} = await {typeof(QueryableExtensions).FullNameInCode()}.{nameof(QueryableExtensions.ToListAsync)}({_session!.Usage}.{nameof(IQuerySession.Query)}<{_entityType.FullNameInCode()}>(), {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IQuerySession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}
