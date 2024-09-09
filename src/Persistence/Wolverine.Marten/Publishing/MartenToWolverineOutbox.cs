using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineOutbox : IMessageOutbox
{
    private readonly Lazy<IWolverineRuntime> _runtime;

    public MartenToWolverineOutbox(IServiceProvider services)
    {
        _runtime = new Lazy<IWolverineRuntime>(() => services.GetRequiredService<IWolverineRuntime>());
    }

    public async ValueTask<IMessageBatch> CreateBatch(DocumentSessionBase session)
    {
        var context = new MessageContext(_runtime.Value, session.TenantId);
        await context.EnlistInOutboxAsync(new MartenEnvelopeTransaction(session, context));
        
        return new MartenToWolverineMessageBatch(context, session);
    }
}