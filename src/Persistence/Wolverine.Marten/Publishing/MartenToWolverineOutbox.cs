using System;
using System.Threading.Tasks;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineOutbox : IMessageOutbox
{
    private readonly ProjectionOutboxSource _source;

    /// <param name="services">The application container</param>
    /// <param name="storeType">
    ///     The ancillary store's marker type, so envelope writes target that store's own message store
    ///     (GH-2887), or null for the main store
    /// </param>
    public MartenToWolverineOutbox(IServiceProvider services, Type? storeType = null)
    {
        _source = new ProjectionOutboxSource(services, storeType);
    }

    public async ValueTask<IMessageBatch> CreateBatch(DocumentSessionBase session)
    {
        var sink = await _source.StartBatchAsync(session.TenantId, c => new MartenEnvelopeTransaction(session, c));
        return new MartenToWolverineMessageBatch(sink);
    }
}
