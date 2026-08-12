using Microsoft.Extensions.DependencyInjection;
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
    private readonly Lazy<IWolverineRuntime> _runtime;

    public FisherToWolverineOutbox(IServiceProvider services)
    {
        _runtime = new Lazy<IWolverineRuntime>(() => services.GetRequiredService<IWolverineRuntime>());
    }

    public async ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
    {
        var context = new MessageContext(_runtime.Value, session.TenantId)
        {
            MultiFlushMode = MultiFlushMode.AllowMultiples
        };

        await context.EnlistInOutboxAsync(new FisherEnvelopeTransaction(session, context));

        return new FisherToWolverineMessageBatch(context, session);
    }
}
