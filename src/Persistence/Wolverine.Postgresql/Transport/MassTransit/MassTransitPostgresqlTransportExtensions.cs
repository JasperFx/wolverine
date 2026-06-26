using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Postgresql.Transport.MassTransit;

public static class MassTransitPostgresqlTransportExtensions
{
    /// <summary>
    /// Find or add the MassTransit PostgreSQL interop transport for this application.
    /// Requires Wolverine to also be using PostgreSQL backed message persistence.
    /// </summary>
    internal static MassTransitPostgresqlTransport MassTransitPostgresqlTransport(this WolverineOptions options,
        string? schema = null)
    {
        var transport = options.Transports.OfType<MassTransitPostgresqlTransport>().FirstOrDefault();
        if (transport == null)
        {
            transport = new MassTransitPostgresqlTransport();
            options.Transports.Add(transport);

            // NOTE: unlike the NServiceBus interop transport, MassTransit owns and migrates its
            // own "transport" schema, so we deliberately do NOT register an IDatabase here.
        }

        if (schema.IsNotEmpty())
        {
            transport.SchemaName = schema!;
        }

        return transport;
    }

    /// <summary>
    /// Opt into the MassTransit PostgreSQL interop transport and configure transport-wide
    /// settings. Calling this is optional; <see cref="ListenToMassTransitPostgresqlQueue"/> and
    /// <see cref="ToMassTransitPostgresqlQueue"/> will register the transport on demand.
    /// </summary>
    /// <param name="schema">Schema that owns the MassTransit transport functions; defaults to "transport"</param>
    /// <param name="autoProvision">
    /// When true, Wolverine will create the MassTransit queue (via <c>create_queue_v2</c>) if it
    /// does not already exist. Defaults to false because MassTransit normally provisions its own.
    /// </param>
    public static WolverineOptions UseMassTransitPostgresqlInterop(this WolverineOptions options,
        string? schema = null, bool autoProvision = false)
    {
        var transport = options.MassTransitPostgresqlTransport(schema);
        transport.AutoProvision = autoProvision;
        return options;
    }

    /// <summary>
    /// Listen for messages published by a MassTransit endpoint to a PostgreSQL queue of the given
    /// name. The queue is owned by MassTransit; Wolverine leases messages via its functions.
    /// </summary>
    /// <param name="queueName">The MassTransit queue name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the MassTransit transport; defaults to "transport"</param>
    public static MassTransitPostgresqlListenerConfiguration ListenToMassTransitPostgresqlQueue(
        this WolverineOptions options, string queueName, string? schema = null)
    {
        var transport = options.MassTransitPostgresqlTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new MassTransitPostgresqlListenerConfiguration(queue);
    }

    /// <summary>
    /// Publish matching messages straight to a MassTransit-owned PostgreSQL queue using its
    /// <c>transport.send_message</c> function, encoded in the MassTransit message model.
    /// </summary>
    /// <param name="queueName">The MassTransit queue name (matched verbatim)</param>
    /// <param name="schema">Optional schema for the MassTransit transport; defaults to "transport"</param>
    public static MassTransitPostgresqlSubscriberConfiguration ToMassTransitPostgresqlQueue(
        this IPublishToExpression publishing, string queueName, string? schema = null)
    {
        var options = publishing.As<PublishingExpression>().Parent;
        var transport = options.MassTransitPostgresqlTransport(schema);
        var queue = transport.Queues[queueName];
        queue.EndpointName = queueName;

        publishing.To(queue.Uri);

        return new MassTransitPostgresqlSubscriberConfiguration(queue);
    }
}
