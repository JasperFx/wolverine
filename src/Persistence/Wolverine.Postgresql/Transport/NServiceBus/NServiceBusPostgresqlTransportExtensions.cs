using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Configuration;

namespace Wolverine.Postgresql.Transport.NServiceBus;

public static class NServiceBusPostgresqlTransportExtensions
{
    /// <summary>
    /// Find or add the NServiceBus PostgreSQL interop transport for this application.
    /// Requires Wolverine to also be using PostgreSQL backed message persistence.
    /// </summary>
    internal static NServiceBusPostgresqlTransport NServiceBusPostgresqlTransport(this WolverineOptions options,
        string? schema = null)
    {
        var transport = options.Transports.OfType<NServiceBusPostgresqlTransport>().FirstOrDefault();
        if (transport == null)
        {
            transport = new NServiceBusPostgresqlTransport();
            options.Transports.Add(transport);

            // Expose the NServiceBus queue tables to the Weasel resource model / command line.
            options.Services.AddTransient<IDatabase, NServiceBusPostgresqlTransportDatabase>();
        }

        if (schema.IsNotEmpty())
        {
            transport.SchemaName = schema!;
        }

        return transport;
    }

    /// <summary>
    /// Opt into the NServiceBus PostgreSQL interop transport and configure transport-wide
    /// settings. Calling this is optional; <see cref="ListenToNServiceBusPostgresqlQueue"/> and
    /// <see cref="ToNServiceBusPostgresqlQueue"/> will register the transport on demand.
    /// </summary>
    /// <param name="schema">Schema that owns the NServiceBus queue tables; defaults to "public"</param>
    /// <param name="autoProvision">
    /// When true, Wolverine will create the NServiceBus queue tables if they do not already
    /// exist. Defaults to false because NServiceBus normally owns and provisions its own tables.
    /// </param>
    public static WolverineOptions UseNServiceBusPostgresqlInterop(this WolverineOptions options,
        string? schema = null, bool autoProvision = false)
    {
        var transport = options.NServiceBusPostgresqlTransport(schema);
        transport.AutoProvision = autoProvision;
        return options;
    }

    /// <summary>
    /// Listen for messages published by an NServiceBus endpoint to a PostgreSQL queue table
    /// of the given name. The table is owned by NServiceBus; Wolverine reads it directly.
    /// </summary>
    /// <param name="queueName">The NServiceBus queue/table name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the NServiceBus queue tables; defaults to "public"</param>
    public static NServiceBusPostgresqlListenerConfiguration ListenToNServiceBusPostgresqlQueue(
        this WolverineOptions options, string queueName, string? schema = null)
    {
        var transport = options.NServiceBusPostgresqlTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new NServiceBusPostgresqlListenerConfiguration(queue);
    }

    /// <summary>
    /// Publish matching messages straight to an NServiceBus-owned PostgreSQL queue table
    /// using the queue name, encoded in the NServiceBus SQL transport wire format.
    /// </summary>
    /// <param name="queueName">The NServiceBus queue/table name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the NServiceBus queue tables; defaults to "public"</param>
    public static NServiceBusPostgresqlSubscriberConfiguration ToNServiceBusPostgresqlQueue(
        this IPublishToExpression publishing, string queueName, string? schema = null)
    {
        var options = publishing.As<PublishingExpression>().Parent;
        var transport = options.NServiceBusPostgresqlTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;

        publishing.To(queue.Uri);

        return new NServiceBusPostgresqlSubscriberConfiguration(queue);
    }
}
