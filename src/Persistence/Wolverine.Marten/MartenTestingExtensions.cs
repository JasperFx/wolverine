using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Marten;

public static class MartenTestingExtensions
{
    /// <summary>
    /// Saves changes to the Marten document session and waits for all outgoing messages to be flushed.
    /// </summary>
    /// <remarks>
    /// This method provides an extension for IHost that initializes a new document session within the
    /// given host's service scope, applies the provided actions to the session, and ensures all changes are saved.
    /// After saving, it explicitly flushes any outgoing messages to guarantee that all message side-effects
    /// are completed before the task completes. This method should be used in testing environments where
    /// immediate consistency of the session and the outgoing message pipeline is required.
    /// </remarks>
    /// <example>
    /// Here is how you can use the <see cref="SaveInMartenAndWaitForOutgoingMessagesAsync"/> extension method within your tests:
    /// <code>
    /// await host.SaveInMartenAndWaitForOutgoingMessagesAsync(session =>
    /// {
    ///     // Perform actions on the session such as saving events
    ///     session.Events.Append(_request.ConsultatieId, new QuestStarted());
    /// });
    /// </code>
    /// </example>
    #region sample_save_in_martend_and_wait_for_outgoing_messages
    public static Task<ITrackedSession> SaveInMartenAndWaitForOutgoingMessagesAsync(this IHost host, Action<IDocumentSession> action, int timeoutInMilliseconds = 5000)
    {
        var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();

        return host.ExecuteAndWaitAsync(async context =>
        {
            var session = factory.OpenSession(context);
            action(session);
            await session.SaveChangesAsync();

            // Shouldn't be necessary, but real life says do it anyway
            await context.As<MessageContext>().FlushOutgoingMessagesAsync();
        }, timeoutInMilliseconds);
    }
    #endregion
}