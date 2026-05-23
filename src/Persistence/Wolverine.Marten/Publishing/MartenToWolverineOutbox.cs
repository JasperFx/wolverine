using System;
using System.Threading.Tasks;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineOutbox : IMessageOutbox
{
    private readonly Lazy<IWolverineRuntime> _runtime;

    // When this outbox belongs to an ancillary Marten store, this is that store's marker
    // type so envelope writes target the ancillary store's own message store. Null for the
    // main store (which uses the runtime's default message store).
    private readonly Type? _storeType;

    public MartenToWolverineOutbox(IServiceProvider services, Type? storeType = null)
    {
        _runtime = new Lazy<IWolverineRuntime>(() => services.GetRequiredService<IWolverineRuntime>());
        _storeType = storeType;
    }

    public async ValueTask<IMessageBatch> CreateBatch(DocumentSessionBase session)
    {
        var context = new MessageContext(_runtime.Value, session.TenantId)
        {
            MultiFlushMode = MultiFlushMode.AllowMultiples
        };

        // For an ancillary Marten store, envelope storage must target THAT store's message
        // store (its own SchemaName / database), not the runtime's default (main) store.
        // Otherwise projection-batch outbox writes land in the main store's schema, which
        // may not exist on the ancillary database. See GH-2887.
        if (_storeType is not null)
        {
            context.OverrideStorage(_runtime.Value.Stores.FindAncillaryStore(_storeType));
        }

        await context.EnlistInOutboxAsync(new MartenEnvelopeTransaction(session, context));

        return new MartenToWolverineMessageBatch(context, session);
    }
}