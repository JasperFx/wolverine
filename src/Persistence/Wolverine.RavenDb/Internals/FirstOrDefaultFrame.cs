using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals;

/// <summary>
///     Emits <c>await session.Query&lt;T&gt;().FirstOrDefaultAsync(token)</c> for a
///     <see cref="Wolverine.Persistence.FirstOrDefaultAttribute" /> parameter.
/// </summary>
/// <remarks>
///     The extension class and method are referenced through <c>typeof</c> / <c>nameof</c> rather than as literal
///     strings so that a rename in the RavenDb client breaks this build instead of shipping a codegen failure that
///     only surfaces the first time an endpoint using the attribute is compiled at runtime. That matters more here
///     than for the other providers, since the RavenDb suite only runs on CI.
/// </remarks>
internal class FirstOrDefaultFrame : AsyncFrame
{
    private readonly Type _entityType;
    private Variable? _session;
    private Variable? _cancellation;

    public FirstOrDefaultFrame(Type entityType)
    {
        _entityType = entityType;
        Result = new Variable(entityType, $"firstOrDefault_{entityType.Name}", this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Read the first {_entityType.NameInCode()} in the database, if any");
        writer.Write(
            $"var {Result.Usage} = await {typeof(LinqExtensions).FullNameInCode()}.{nameof(LinqExtensions.FirstOrDefaultAsync)}({_session!.Usage}.Query<{_entityType.FullNameInCode()}>(), {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IAsyncDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}
