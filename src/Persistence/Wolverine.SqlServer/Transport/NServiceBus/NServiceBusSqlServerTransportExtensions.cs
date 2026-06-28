using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Configuration;

namespace Wolverine.SqlServer.Transport.NServiceBus;

public static class NServiceBusSqlServerTransportExtensions
{
    /// <summary>
    /// Find or add the NServiceBus SQL Server interop transport for this application.
    /// Requires Wolverine to also be using SQL Server backed message persistence.
    /// </summary>
    internal static NServiceBusSqlServerTransport NServiceBusSqlServerTransport(this WolverineOptions options,
        string? schema = null, string? connectionString = null)
    {
        var transport = options.Transports.OfType<NServiceBusSqlServerTransport>().FirstOrDefault();
        if (transport == null)
        {
            transport = new NServiceBusSqlServerTransport();
            options.Transports.Add(transport);

            // Expose the NServiceBus queue tables to the Weasel resource model / command line.
            options.Services.AddTransient<IDatabase, NServiceBusSqlServerTransportDatabase>();
        }

        if (schema.IsNotEmpty())
        {
            transport.SchemaName = schema!;
        }

        if (connectionString.IsNotEmpty())
        {
            transport.ConnectionString = connectionString;
        }

        return transport;
    }

    /// <summary>
    /// Opt into the NServiceBus SQL Server interop transport and configure transport-wide
    /// settings. Calling this is optional; <see cref="ListenToNServiceBusSqlServerQueue"/> and
    /// <see cref="ToNServiceBusSqlServerQueue"/> will register the transport on demand.
    /// </summary>
    /// <param name="schema">Schema that owns the NServiceBus queue tables; defaults to "dbo"</param>
    /// <param name="autoProvision">
    /// When true, Wolverine will create the NServiceBus queue tables if they do not already
    /// exist. Defaults to false because NServiceBus normally owns and provisions its own tables.
    /// </param>
    /// <param name="connectionString">
    /// Optional explicit connection string for the single database that owns the NServiceBus interop queue tables.
    /// Set this when Wolverine's message storage is multi-tenanted (a database per tenant) but the NServiceBus queues
    /// live on one shared database: the transport binds to this one database only and never creates a queue per tenant
    /// database. When null (the default), the transport uses Wolverine's SQL Server message store (the <c>Main</c>
    /// store under multi-tenancy).
    /// </param>
    public static WolverineOptions UseNServiceBusSqlServerInterop(this WolverineOptions options,
        string? schema = null, bool autoProvision = false, string? connectionString = null)
    {
        var transport = options.NServiceBusSqlServerTransport(schema, connectionString);
        transport.AutoProvision = autoProvision;
        return options;
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
