using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals;

/// <summary>
///     Emits <c>await session.Query&lt;T&gt;().ToListAsync(token)</c> for a
///     <see cref="Wolverine.Persistence.AllAttribute" /> parameter.
/// </summary>
/// <remarks>
///     The extension class and method are referenced through <c>typeof</c> / <c>nameof</c> rather than as literal
///     strings so that a rename in the RavenDb client breaks this build instead of shipping a codegen failure that
///     only surfaces the first time an endpoint using the attribute is compiled at runtime. That matters more here
///     than for the other providers, since the RavenDb suite only runs on CI.
/// </remarks>
internal class AllFrame : AsyncFrame
{
    private readonly Type _entityType;
    private Variable? _session;
    private Variable? _cancellation;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IReadOnlyList<>/IQueryable<> over the element type at CODEGEN time only. AOT consumers run pre-generated code in TypeLoadMode.Static, so this never fires in a published app. See the AOT guide.")]
    public AllFrame(Type entityType)
    {
        _entityType = entityType;
        Result = new Variable(typeof(IReadOnlyList<>).MakeGenericType(entityType), $"all_{entityType.Name}", this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Read every {_entityType.NameInCode()} in the database");
        writer.Write($"var {Result.Usage} = await {typeof(LinqExtensions).FullNameInCode()}.{nameof(LinqExtensions.ToListAsync)}({_session!.Usage}.Query<{_entityType.FullNameInCode()}>(), {_cancellation!.Usage}).ConfigureAwait(false);");

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
