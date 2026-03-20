using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Polecat;

public static class PolecatTestingExtensions
{
    /// <summary>
    /// Saves changes to the Polecat document session and waits for all outgoing messages to be flushed.
    /// </summary>
    /// <remarks>
    /// This method provides an extension for IHost that initializes a new document session within the
    /// given host's service scope, applies the provided actions to the session, and ensures all changes are saved.
    /// After saving, it explicitly flushes any outgoing messages to guarantee that all message side-effects
    /// are completed before the task completes.
    /// </remarks>
    /// <example>
    /// <code>
    /// await host.SaveInPolecatAndWaitForOutgoingMessagesAsync(session =>
    /// {
    ///     session.Events.Append(streamId, new OrderPlaced());
    /// });
    /// </code>
    /// </example>
    public static Task<ITrackedSession> SaveInPolecatAndWaitForOutgoingMessagesAsync(this IHost host,
        Action<IDocumentSession> action, int timeoutInMilliseconds = 5000)
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
}
