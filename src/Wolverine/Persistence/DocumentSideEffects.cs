using System.Linq.Expressions;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

// GH-5143: the store agnostic document and stream operations that MartenOps, PolecatOps and FisherOps each
// carried their own copy of. Each is written against the JasperFx contracts (IDocumentWriteOperations,
// IEventStoreOperations) that Marten, Polecat and Fisher all implement, so a handler returning one is valid on
// any of them. Built through Storage.DeleteWhere() and friends. Like AppendEvents and StartStream, these enroll
// the chain in the store's transaction themselves, because a store agnostic handler names no store type for
// AutoApplyTransactions to recognize.
//
// ArchiveEventStream / TombstoneEventStream are not named ArchiveStream / TombstoneStream on purpose: Marten,
// Polecat and Fisher each ship a public type of that name, and code importing both namespaces would no longer
// compile (CS0104).

/// <summary>
///     Delete every document of type <typeparamref name="T" /> matching an expression, as a Wolverine side
///     effect. Build through <see cref="Storage.DeleteWhere{T}" />.
/// </summary>
public class DeleteWhere<T> : ISideEffectAware where T : notnull
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(DeleteWhere<T>), chain, variable, rules, container);

    internal DeleteWhere(Expression<Func<T, bool>> expression)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>
    ///     Which documents to delete.
    /// </summary>
    public Expression<Func<T, bool>> Expression { get; }

    public void Execute(IDocumentWriteOperations operations)
    {
        operations.DeleteWhere(Expression);
    }
}

/// <summary>
///     Delete one document of type <typeparamref name="T" /> by its identity, as a Wolverine side effect. Build
///     through <see cref="Storage.DeleteById{T}(Guid)" /> or <see cref="Storage.DeleteById{T}(string)" />.
/// </summary>
public class DeleteById<T> : ISideEffectAware where T : notnull
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(DeleteById<T>), chain, variable, rules, container);

    internal DeleteById(Guid id)
    {
        Id = id;
    }

    internal DeleteById(string id)
    {
        Key = id ?? throw new ArgumentNullException(nameof(id));
    }

    /// <summary>
    ///     The Guid identity of the document, for Guid identified documents.
    /// </summary>
    public Guid? Id { get; }

    /// <summary>
    ///     The string identity of the document, for string identified documents.
    /// </summary>
    public string? Key { get; }

    public void Execute(IDocumentWriteOperations operations)
    {
        if (Id.HasValue)
        {
            operations.Delete<T>(Id.Value);
        }
        else
        {
            operations.Delete<T>(Key!);
        }
    }
}

/// <summary>
///     Mark an event stream as archived, as a Wolverine side effect. Build through
///     <see cref="Storage.ArchiveStream(Guid)" /> or <see cref="Storage.ArchiveStream(string)" />.
/// </summary>
public class ArchiveEventStream : ISideEffectAware
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(ArchiveEventStream), chain, variable, rules, container);

    internal ArchiveEventStream(Guid streamId)
    {
        StreamId = streamId;
    }

    internal ArchiveEventStream(string streamKey)
    {
        StreamKey = streamKey ?? throw new ArgumentNullException(nameof(streamKey));
    }

    /// <summary>
    ///     The Guid identity of the stream, for stores configured with Guid stream identity.
    /// </summary>
    public Guid? StreamId { get; }

    /// <summary>
    ///     The string key of the stream, for stores configured with string stream identity.
    /// </summary>
    public string? StreamKey { get; }

    public void Execute(IEventStoreOperations operations)
    {
        if (StreamId.HasValue)
        {
            operations.ArchiveStream(StreamId.Value);
        }
        else
        {
            operations.ArchiveStream(StreamKey!);
        }
    }
}

/// <summary>
///     Permanently delete an event stream and all of its events, as a Wolverine side effect. Build through
///     <see cref="Storage.TombstoneStream(Guid)" /> or <see cref="Storage.TombstoneStream(string)" />.
/// </summary>
public class TombstoneEventStream : ISideEffectAware
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(TombstoneEventStream), chain, variable, rules, container);

    internal TombstoneEventStream(Guid streamId)
    {
        StreamId = streamId;
    }

    internal TombstoneEventStream(string streamKey)
    {
        StreamKey = streamKey ?? throw new ArgumentNullException(nameof(streamKey));
    }

    /// <summary>
    ///     The Guid identity of the stream, for stores configured with Guid stream identity.
    /// </summary>
    public Guid? StreamId { get; }

    /// <summary>
    ///     The string key of the stream, for stores configured with string stream identity.
    /// </summary>
    public string? StreamKey { get; }

    public void Execute(IEventStoreOperations operations)
    {
        if (StreamId.HasValue)
        {
            operations.TombstoneStream(StreamId.Value);
        }
        else
        {
            operations.TombstoneStream(StreamKey!);
        }
    }
}
