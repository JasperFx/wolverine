using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.SqlServer.Transport.NServiceBus;

public static class NServiceBusSqlServerTransportExtensions
{
    /// <summary>
    /// Find or add the NServiceBus SQL Server interop transport for this application.
    /// Requires Wolverine to also be using SQL Server backed message persistence.
    /// </summary>
    internal static NServiceBusSqlServerTransport NServiceBusSqlServerTransport(this WolverineOptions options,
        string? schema = null)
    {
        var transport = options.Transports.OfType<NServiceBusSqlServerTransport>().FirstOrDefault();
        if (transport == null)
        {
            transport = new NServiceBusSqlServerTransport();
            options.Transports.Add(transport);
        }

        if (schema.IsNotEmpty())
        {
            transport.SchemaName = schema!;
        }

        return transport;
    }

    /// <summary>
    /// Listen for messages published by an NServiceBus endpoint to a SQL Server queue table
    /// of the given name. The table is owned by NServiceBus; Wolverine reads it directly.
    /// </summary>
    /// <param name="queueName">The NServiceBus queue/table name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the NServiceBus queue tables; defaults to "dbo"</param>
    public static NServiceBusSqlServerListenerConfiguration ListenToNServiceBusSqlServerQueue(
        this WolverineOptions options, string queueName, string? schema = null)
    {
        var transport = options.NServiceBusSqlServerTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new NServiceBusSqlServerListenerConfiguration(queue);
    }

    /// <summary>
    /// Publish matching messages straight to an NServiceBus-owned SQL Server queue table
    /// using the queue name, encoded in the NServiceBus SQL transport wire format.
    /// </summary>
    /// <param name="queueName">The NServiceBus queue/table name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the NServiceBus queue tables; defaults to "dbo"</param>
    public static NServiceBusSqlServerSubscriberConfiguration ToNServiceBusSqlServerQueue(
        this IPublishToExpression publishing, string queueName, string? schema = null)
    {
        var options = publishing.As<PublishingExpression>().Parent;
        var transport = options.NServiceBusSqlServerTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;

        publishing.To(queue.Uri);

        return new NServiceBusSqlServerSubscriberConfiguration(queue);
    }
}
