using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.InMemory.Codegen;

/// <summary>
/// The in-memory store's spelling of loading an aggregate for the aggregate handler workflow:
/// <c>session.Events.FetchForWriting&lt;T&gt;(id)</c>, through the store-agnostic event store API.
/// </summary>
internal class LoadAggregateFrame : AsyncFrame
{
    private readonly AggregateLoadRequest _request;
    private readonly Variable _identity;
    private readonly Variable _rawIdentity;
    private readonly Variable? _version;
    private Variable? _session;
    private Variable? _token;

    public LoadAggregateFrame(AggregateLoadRequest request)
    {
        if (request.IsNaturalKey)
        {
            throw new NotSupportedException(
                $"Natural keys are not supported by the in-memory prototyping store (aggregate {request.AggregateType.FullNameInCode()}). " +
                "Switch to Marten, Polecat or Fisher when you need them.");
        }

        _request = request;
        _identity = request.AggregateId;

        if (request is { LoadStyle: ModelConcurrencyStyle.Optimistic, Version: not null })
        {
            _version = request.Version;
        }

        Stream = new Variable(typeof(IEventStream<>).MakeGenericType(request.AggregateType), this);
        _rawIdentity = rawIdentityOf(_identity);
    }

    public Variable Stream { get; }

    internal static Variable rawIdentityOf(Variable identity)
    {
        if (identity.VariableType == typeof(Guid) || identity.VariableType == typeof(string)) return identity;

        var valueType = ValueTypeInfo.ForType(identity.VariableType);
        return new MemberAccessVariable(identity, valueType.ValueProperty);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _identity;
        if (_version != null) yield return _version;

        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Loading the aggregate from the in-memory prototyping store");

        var events = $"(({typeof(IDocumentSessionOperations).FullNameInCode()}){_session!.Usage}).{nameof(IDocumentSessionOperations.Events)}";
        var aggregate = _request.AggregateType.FullNameInCode();

        var call = _request.LoadStyle == ModelConcurrencyStyle.Exclusive
            ? $"{nameof(IEventStoreOperations.FetchForExclusiveWriting)}<{aggregate}>({_rawIdentity.Usage}, {_token!.Usage})"
            : _version == null
                ? $"{nameof(IEventStoreOperations.FetchForWriting)}<{aggregate}>({_rawIdentity.Usage}, {_token!.Usage})"
                : $"{nameof(IEventStoreOperations.FetchForWriting)}<{aggregate}>({_rawIdentity.Usage}, {_version.Usage}, {_token!.Usage})";

        writer.WriteLine($"var {Stream.Usage} = await {events}.{call};");

        if (_request.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} = true;");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// <c>session.Events.FetchLatest&lt;T&gt;(id)</c>: the current state of an aggregate, for a read model.
/// </summary>
internal class FetchLatestAggregateFrame : AsyncFrame
{
    private readonly Variable _identity;
    private Variable _session = null!;
    private Variable _token = null!;

    public FetchLatestAggregateFrame(Type aggregateType, Variable identity)
    {
        _identity = LoadAggregateFrame.rawIdentityOf(identity);
        Aggregate = new Variable(aggregateType, this);
    }

    public Variable Aggregate { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var events = $"(({typeof(IDocumentSessionOperations).FullNameInCode()}){_session.Usage}).{nameof(IDocumentSessionOperations.Events)}";
        writer.Write(
            $"var {Aggregate.Usage} = await {events}.{nameof(IEventStoreOperations.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
