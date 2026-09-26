using JasperFx.Events;
using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

/// <summary>
///     Bridges Marten's projection side effect publishing to Wolverine's outgoing message
///     machinery. All of the behavior lives in the store agnostic
///     <see cref="ProjectionSideEffectSink" /> so that this bridge, Polecat's and Fisher's stay
///     identical -- they diverged once already, which is how GH-4556 shipped fixed on one store
///     and broken on the other two.
/// </summary>
internal class MartenToWolverineMessageBatch : IMessageBatch
{
    private readonly ProjectionSideEffectSink _sink;

    public MartenToWolverineMessageBatch(MessageContext context, DocumentSessionBase session)
    {
        _sink = new ProjectionSideEffectSink(context, c => new MartenEnvelopeTransaction(session, c));
    }

    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return _sink.PublishAsync(message, new MessageMetadata(tenantId));
    }

    /// <summary>
    ///     Metadata-aware overload backing <see cref="IMessageSink.PublishAsync{T}(T, MessageMetadata)" />
    ///     (JasperFx.Events 1.29+).
    /// </summary>
    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
    {
        return _sink.PublishAsync(message, metadata);
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _sink.FlushAsync();
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
