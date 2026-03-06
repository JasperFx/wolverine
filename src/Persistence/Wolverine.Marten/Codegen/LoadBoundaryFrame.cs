using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;
using Marten;
using Marten.Events.Dcb;

namespace Wolverine.Marten.Codegen;

internal class LoadBoundaryFrame : AsyncFrame
{
    private readonly Type _aggregateType;
    private Variable? _query;
    private Variable? _session;
    private Variable? _token;
    private readonly Type _boundaryType;

    public LoadBoundaryFrame(Type aggregateType, Variable? query = null)
    {
        _aggregateType = aggregateType;
        _query = query;
        _boundaryType = typeof(IEventBoundary<>).MakeGenericType(aggregateType);
        Boundary = new Variable(_boundaryType, this);
    }

    public Variable Boundary { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _query ??= chain.FindVariable(typeof(EventTagQuery));
        yield return _query;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Loading DCB boundary model via FetchForWritingByTags");
        writer.WriteLine(
            $"var {Boundary.Usage} = await {_session!.Usage}.Events.FetchForWritingByTags<{_aggregateType.FullNameInCode()}>({_query.Usage}, {_token!.Usage});");

        Next?.GenerateCode(method, writer);
    }
}
