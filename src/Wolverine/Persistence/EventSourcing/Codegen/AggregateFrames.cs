using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing.Codegen;

// GH-5141: the aggregate handler workflow's store-side frames, written once for Marten, Polecat and Fisher. They
// differed only by the session type they look for (each store's own IDocumentSession) and the store name in a
// comment; the generated text, "{session}.Events.FetchForWriting<T>(...)" and friends, was the same in all three.
// Each store hands these back from its IEventSourcingFrameProvider. Marten derives its batchable variants from
// them (MartenBatchingPolicy enlists those in a batched query); Polecat and Fisher use them as they are, and get
// the F# rendering Marten already had for free.

/// <summary>
///     Loads the event stream for the aggregate handler workflow with
///     <c>session.Events.FetchForWriting</c> / <c>FetchForExclusiveWriting</c>.
/// </summary>
internal class LoadAggregateFrame : AsyncFrame
{
    protected readonly AggregateLoadRequest _att;
    protected readonly Type _sessionType;
    protected readonly string _storeName;
    protected Variable? _session;
    protected Variable? _token;
    protected readonly Variable _identity;
    protected readonly Variable? _version;
    protected readonly Type _eventStreamType;
    protected readonly Variable _rawIdentity;

    /// <param name="att">The aggregate load the workflow asked for</param>
    /// <param name="sessionType">The store's own document session type, which the chain resolves</param>
    /// <param name="storeName">The store's name, for the generated comment</param>
    // Codegen-time only: closes IEventStream<T> over the aggregate type and reads a strong-typed id's shape, both
    // rooted by handler discovery. Suppressed wholesale in each integration's GlobalSuppressions before GH-5141.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Codegen-time only; see comment.")]
    public LoadAggregateFrame(AggregateLoadRequest att, Type sessionType, string storeName)
    {
        _att = att;
        _sessionType = sessionType;
        _storeName = storeName;
        _identity = _att.AggregateId;

        if (_att is { LoadStyle: ModelConcurrencyStyle.Optimistic, Version: not null })
        {
            _version = _att.Version;
        }

        _eventStreamType = typeof(IEventStream<>).MakeGenericType(_att.AggregateType);
        Stream = new Variable(_eventStreamType, this);

        _rawIdentity = _identity;
        // For natural keys, keep the full natural key object (don't unwrap)
        if (!_att.IsNaturalKey && _rawIdentity.VariableType != typeof(Guid) && _rawIdentity.VariableType != typeof(string))
        {
            var valueType = ValueTypeInfo.ForType(_rawIdentity.VariableType);
            _rawIdentity = new MemberAccessVariable(_identity, valueType.ValueProperty);
        }
    }

    public Variable Stream { get; }
    public bool IsNaturalKey => _att.IsNaturalKey;

    protected string Comment => $"Loading {_storeName} aggregate as part of the aggregate handler workflow";

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _identity;
        if (_version != null) yield return _version;

        _session = chain.FindVariable(_sessionType);
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(Comment);
        writeFetch(writer);
        writeConsistency(writer);
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(Comment);
        writeFSharpFetch(writer);
        writeFSharpConsistency(writer);
        Next?.GenerateFSharpCode(method, writer);
    }

    protected void writeFetch(ISourceWriter writer)
    {
        var aggregateType = _att.AggregateType.FullNameInCode();
        if (_att.IsNaturalKey)
        {
            var nkType = _identity.VariableType.FullNameInCode();
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{aggregateType}, {nkType}>({_identity.Usage}, {_token!.Usage});");
        }
        else if (_att.LoadStyle == ModelConcurrencyStyle.Exclusive)
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForExclusiveWriting<{aggregateType}>({_rawIdentity.Usage}, {_token!.Usage});");
        }
        else if (_version == null)
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{aggregateType}>({_rawIdentity.Usage}, {_token!.Usage});");
        }
        else
        {
            writer.WriteLine($"var {Stream.Usage} = await {_session!.Usage}.Events.FetchForWriting<{aggregateType}>({_rawIdentity.Usage}, {_version.Usage}, {_token!.Usage});");
        }
    }

    // `var x = await ...` -> F# `let! x = ...` inside the task { } computation expression
    protected void writeFSharpFetch(ISourceWriter writer)
    {
        var aggregateType = _att.AggregateType.FSharpName();
        if (_att.IsNaturalKey)
        {
            var nkType = _identity.VariableType.FSharpName();
            writer.WriteLine($"let! {Stream.Usage} = {_session!.FSharpUsage}.Events.FetchForWriting<{aggregateType}, {nkType}>({_identity.FSharpUsage}, {_token!.FSharpUsage})");
        }
        else if (_att.LoadStyle == ModelConcurrencyStyle.Exclusive)
        {
            writer.WriteLine($"let! {Stream.Usage} = {_session!.FSharpUsage}.Events.FetchForExclusiveWriting<{aggregateType}>({_rawIdentity.FSharpUsage}, {_token!.FSharpUsage})");
        }
        else if (_version == null)
        {
            writer.WriteLine($"let! {Stream.Usage} = {_session!.FSharpUsage}.Events.FetchForWriting<{aggregateType}>({_rawIdentity.FSharpUsage}, {_token!.FSharpUsage})");
        }
        else
        {
            writer.WriteLine($"let! {Stream.Usage} = {_session!.FSharpUsage}.Events.FetchForWriting<{aggregateType}>({_rawIdentity.FSharpUsage}, {_version.FSharpUsage}, {_token!.FSharpUsage})");
        }
    }

    protected void writeConsistency(ISourceWriter writer)
    {
        if (_att.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} = true;");
        }
    }

    protected void writeFSharpConsistency(ISourceWriter writer)
    {
        if (_att.AlwaysEnforceConsistency)
        {
            writer.WriteLine($"{Stream.Usage}.{nameof(IEventStream<string>.AlwaysEnforceConsistency)} <- true");
        }
    }
}

/// <summary>
///     The <c>[ReadModel]</c> / <c>[ReadAggregate]</c> read: <c>session.Events.FetchLatest&lt;T&gt;(id)</c>.
/// </summary>
internal class FetchLatestAggregateFrame : AsyncFrame
{
    protected readonly Type _sessionType;
    protected readonly Variable _identity;
    protected Variable _session = null!;
    protected Variable _token = null!;

    // Codegen-time only: closes IEventStream<T> over the aggregate type and reads a strong-typed id's shape, both
    // rooted by handler discovery. Suppressed wholesale in each integration's GlobalSuppressions before GH-5141.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Codegen-time only; see comment.")]
    public FetchLatestAggregateFrame(Type aggregateType, Variable identity, Type sessionType)
    {
        _sessionType = sessionType;

        if (identity.VariableType == typeof(Guid) || identity.VariableType == typeof(string))
        {
            _identity = identity;
        }
        else
        {
            var valueType = ValueTypeInfo.ForType(identity.VariableType);
            _identity = new MemberAccessVariable(identity, valueType.ValueProperty);
        }

        Aggregate = new Variable(aggregateType, this);
    }

    public Variable Aggregate { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(_sessionType);
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Aggregate.Usage} = await {_session.Usage}.Events.{nameof(IEventStoreOperations.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"let! {Aggregate.Usage} = {_session.FSharpUsage}.Events.{nameof(IEventStoreOperations.FetchLatest)}<{Aggregate.VariableType.FSharpName()}>({_identity.FSharpUsage}, {_token.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}

/// <summary>
///     GH-3627. The store-side spellings of <c>[StreamState]</c> and <c>[StreamEvents]</c>, reached through
///     <see cref="IEventSourcingFrameProvider" /> so core never writes
///     <c>session.Events.FetchStreamStateAsync(...)</c> into a chain without the store's say-so.
/// </summary>
internal abstract class FetchStreamFrameBase : AsyncFrame
{
    protected readonly Type _sessionType;
    protected readonly Variable _identity;
    protected Variable _session = null!;
    protected Variable _token = null!;

    // Codegen-time only: closes IEventStream<T> over the aggregate type and reads a strong-typed id's shape, both
    // rooted by handler discovery. Suppressed wholesale in each integration's GlobalSuppressions before GH-5141.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Codegen-time only; see comment.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Codegen-time only; see comment.")]
    protected FetchStreamFrameBase(Variable identity, Type readType, Type sessionType)
    {
        _sessionType = sessionType;

        if (identity.VariableType == typeof(Guid) || identity.VariableType == typeof(string))
        {
            _identity = identity;
        }
        else
        {
            var valueType = ValueTypeInfo.ForType(identity.VariableType);
            _identity = new MemberAccessVariable(identity, valueType.ValueProperty);
        }

        Read = new Variable(readType, this);
    }

    public Variable Read { get; }

    /// <summary>
    ///     The <see cref="IQueryEventStore" /> method this frame awaits
    /// </summary>
    protected abstract string StandaloneCall { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(_sessionType);
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Read.Usage} = await {_session.Usage}.Events.{StandaloneCall}({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"let! {Read.Usage} = {_session.FSharpUsage}.Events.{StandaloneCall}({_identity.FSharpUsage}, {_token.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class FetchStreamStateFrame : FetchStreamFrameBase
{
    public FetchStreamStateFrame(Variable identity, Type sessionType) : base(identity, typeof(StreamState), sessionType)
    {
    }

    protected override string StandaloneCall => nameof(IQueryEventStore.FetchStreamStateAsync);
}

internal class FetchStreamFrame : FetchStreamFrameBase
{
    public FetchStreamFrame(Variable identity, Type sessionType) : base(identity, typeof(IReadOnlyList<IEvent>), sessionType)
    {
    }

    protected override string StandaloneCall => nameof(IQueryEventStore.FetchStreamAsync);
}
