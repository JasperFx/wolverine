using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Events.Aggregation;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Publishing;

/// <summary>
///     <see cref="IMessageOutbox"/> implementation that bridges Polecat's projection
///     daemon to Wolverine's outgoing-message machinery. Registered as
///     <see cref="Polecat.StoreOptions.MessageOutbox"/> when
///     <see cref="WolverineOptionsPolecatExtensions.IntegrateWithWolverine"/> runs;
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
    private readonly Lazy<IWolverineRuntime> _runtime;

    public PolecatToWolverineOutbox(IServiceProvider services)
    {
        _runtime = new Lazy<IWolverineRuntime>(() => services.GetRequiredService<IWolverineRuntime>());
    }

    public async ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
    {
        var context = new MessageContext(_runtime.Value, session.TenantId)
        {
            MultiFlushMode = MultiFlushMode.AllowMultiples
        };

        await context.EnlistInOutboxAsync(new PolecatEnvelopeTransaction(session, context));

        return new PolecatToWolverineMessageBatch(context, session);
    }
}
