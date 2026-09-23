using JasperFx.Events;
using Fisher;
using Fisher.Events.Messaging;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Publishing;

/// <summary>
///     Fisher side of the projection-message bridge to Wolverine. One instance per projection
///     daemon batch; <see cref="IMessageOutbox.CreateBatch(IDocumentSession)"/> vends a fresh
///     batch on the first <c>slice.PublishMessage(...)</c> within a given daemon write.
/// </summary>
/// <remarks>
///     Mirrors <see cref="Wolverine.Marten.Publishing.MartenToWolverineMessageBatch"/>. All three
///     bridges now delegate to the store agnostic
///     <see cref="Wolverine.Runtime.ProjectionSideEffectSink"/>, which is what keeps them from
///     drifting: GH-4556 was originally fixed in the Marten bridge only, and these two kept
///     silently dropping every <see cref="ISendMyself"/> published from a projection.
///     The one difference from Marten is that Fisher's
///     <see cref="IMessageBatch.BeforeCommitAsync"/> / <see cref="IMessageBatch.AfterCommitAsync"/>
///     take just a <see cref="CancellationToken"/> — Fisher already owns the session + commit
///     context internally.
/// </remarks>
internal class FisherToWolverineMessageBatch : IMessageBatch
{
    private readonly ProjectionSideEffectSink _sink;

    public FisherToWolverineMessageBatch(MessageContext context, IDocumentSession session)
    {
        _sink = new ProjectionSideEffectSink(context, c => new FisherEnvelopeTransaction(session, c));
    }

    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return _sink.PublishAsync(message, new MessageMetadata(tenantId));
    }

    /// <summary>
    ///     Metadata-aware overload backing <see cref="IMessageSink.PublishAsync{T}(T, MessageMetadata)"/>
    ///     (JasperFx.Events 2.0+). See https://github.com/JasperFx/wolverine/issues/2545.
    /// </summary>
    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
    {
        return _sink.PublishAsync(message, metadata);
    }

    public Task BeforeCommitAsync(CancellationToken token)
    {
        // Wolverine's MessageContext flushes after commit (post-#2545); the Marten bridge does the
        // same. Fisher surfaces a pre-commit hook for future "outbox row participates in the
        // projection SQL transaction" strategies, but the current bridge stays best-effort
        // post-commit.
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(CancellationToken token)
    {
        return _sink.FlushAsync();
    }
}
