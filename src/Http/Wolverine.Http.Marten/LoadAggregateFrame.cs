using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Http;
using Wolverine.Marten;

namespace Wolverine.Http.Marten;

internal class LoadAggregateFrame<T> : AsyncFrame where T : class
{
    private Variable _id;
    private Variable _session;
    private Variable _cancellation;
    private Variable _version;
    private readonly string _methodName;
    private readonly AggregateAttribute _att;

    public LoadAggregateFrame(AggregateAttribute att)
    {
        EventStream = new Variable(typeof(IEventStream<T>), "eventStream", this);
        _methodName = (att.LoadStyle == ConcurrencyStyle.Exclusive) ? nameof(IEventStore.FetchForExclusiveWriting) : nameof(IEventStore.FetchForWriting);
        Type[] argTypes = null;
        _version = att.VersionVariable;
        _id = att.IdVariable;

        _att = att;
    }

    public Variable EventStream { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var versionArg = _version == null ? "" : $"{_version.Usage},";
        writer.Write($"var {EventStream.Usage} = await {_session.Usage}.Events.{_methodName}<{typeof(T).FullNameInCode()}>({_id.Usage}, {versionArg}{_cancellation.Usage});");
        writer.Write($"BLOCK:if ({EventStream.Usage}.Aggregate == null)");
        writer.Write($"await {typeof(Results).FullNameInCode()}.{nameof(Results.NotFound)}().{nameof(IResult.ExecuteAsync)}(httpContext);");
        writer.Write("return;");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _id;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_att.LoadStyle == ConcurrencyStyle.Optimistic && _att.VersionVariable != null)
        {
            _version = _att.VersionVariable;
            yield return _version;
        }
    }
}