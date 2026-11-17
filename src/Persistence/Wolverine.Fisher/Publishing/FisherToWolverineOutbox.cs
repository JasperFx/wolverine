using Fisher;
using Fisher.Events.Messaging;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Publishing;

/// <summary>
///     <see cref="IMessageOutbox"/> implementation that bridges Fisher's projection
///     daemon to Wolverine's outgoing-message machinery. Registered as
///     <see cref="Fisher.StoreOptions.MessageOutbox"/> when
///     <see cref="WolverineOptionsFisherExtensions.IntegrateWithWolverine"/> runs;
///     replaces Fisher's default <see cref="NulloMessageOutbox"/> (which drops
///     every published message).
/// </summary>
/// <remarks>
///     Mirrors <see cref="Wolverine.Marten.Publishing.MartenToWolverineOutbox"/>
///     verbatim except for the Fisher-vs-Marten session type. The
///     <see cref="IMessageOutbox.CreateBatch(IDocumentSession)"/> contract receives
///     the public <see cref="IDocumentSession"/> (Fisher exposes the
///     <c>ITransactionParticipantRegistrar</c> surface needed to enlist there), so
///     no <c>InternalsVisibleTo</c> is required.
/// </remarks>
internal class FisherToWolverineOutbox : IMessageOutbox
{
    private readonly ProjectionOutboxSource _source;

    /// <param name="services">The application container</param>
    /// <param name="storeType">The ancillary store's marker type, or null for the main store (GH-5143)</param>
    public FisherToWolverineOutbox(IServiceProvider services, Type? storeType = null)
    {
        _source = new ProjectionOutboxSource(services, storeType);
    }

    public async ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
    {
        var sink = await _source.StartBatchAsync(session.TenantId, c => new FisherEnvelopeTransaction(session, c));
        return new FisherToWolverineMessageBatch(sink);
    }
}
