using Polecat;
using Polecat.Events.Aggregation;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Publishing;

/// <summary>
///     <see cref="IMessageOutbox"/> implementation that bridges Polecat's projection
///     daemon to Wolverine's outgoing-message machinery. Registered as
///     <see cref="Polecat.StoreOptions.MessageOutbox"/> when
///     <see cref="WolverineOptionsPolecatExtensions.IntegrateWithWolverine(PolecatConfigurationExpression, Action{PolecatIntegration})"/> runs;
///     replaces Polecat's default <see cref="NulloMessageOutbox"/> (which drops
///     every published message).
/// </summary>
/// <remarks>
///     Mirrors <see cref="Wolverine.Marten.Publishing.MartenToWolverineOutbox"/>
///     verbatim except for the Polecat-vs-Marten session type. The
///     <see cref="IMessageOutbox.CreateBatch(IDocumentSession)"/> contract receives
///     the public <see cref="IDocumentSession"/> (Polecat exposes the
///     <c>ITransactionParticipantRegistrar</c> surface needed to enlist there), so
///     no <c>InternalsVisibleTo</c> is required.
/// </remarks>
internal class PolecatToWolverineOutbox : IMessageOutbox
{
    private readonly ProjectionOutboxSource _source;

    /// <param name="services">The application container</param>
    /// <param name="storeType">The ancillary store's marker type, or null for the main store (GH-5143)</param>
    public PolecatToWolverineOutbox(IServiceProvider services, Type? storeType = null)
    {
        _source = new ProjectionOutboxSource(services, storeType);
    }

    public async ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
    {
        var sink = await _source.StartBatchAsync(session.TenantId, c => new PolecatEnvelopeTransaction(session, c));
        return new PolecatToWolverineMessageBatch(sink);
    }
}
