using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weasel.Core.Migrations;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.RateLimiting;
using Wolverine.RDBMS;
using Wolverine.Runtime.Partitioning;
using Wolverine.SqlServer.RateLimiting;
using Wolverine.SqlServer.Schema;
using Wolverine.SqlServer.Transport;

namespace Wolverine.SqlServer;

public static class SqlServerConfigurationExtensions
{
    /// <summary>
    ///     Register sql server backed message persistence to a known connection string
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema">Potentially override the schema name for Wolverine envelope storage. Default is to use WolverineOptions.Durability.MessageStorageSchemaName ?? "dbo". This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    public static ISqlServerBackedPersistence PersistMessagesWithSqlServer(this WolverineOptions options, string connectionString,
        string? schema = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        // For clean idempotency checks
        options.OnException<SqlException>(e => e.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint") &&
                                               e.Message.ContainsIgnoreCase(".wolverine_incoming_envelopes")).Discard();
        
        var extension = new SqlServerBackedPersistence(options)
        {
            ConnectionString = connectionString,
            Role = role
        };

        if (schema.IsNotEmpty())
        {
            extension.EnvelopeStorageSchemaName = schema;
        }
        else
        {
            extension.EnvelopeStorageSchemaName = options.Durability.MessageStorageSchemaName ?? "dbo";
        }
        
        extension.Configure(options);

        return extension;
    }

    /// <summary>
    /// Register SQL Server backed rate limiting storage
    /// </summary>
    public static ISqlServerBackedPersistence UseSqlServerRateLimiting(this ISqlServerBackedPersistence persistence,
        Action<SqlServerRateLimitOptions>? configure = null)
    {
        var options = new SqlServerRateLimitOptions();
        configure?.Invoke(options);

        var concrete = persistence.As<SqlServerBackedPersistence>();
        var schemaName = options.SchemaName ?? concrete.EnvelopeStorageSchemaName;

        concrete.AddStoreConfiguration(store =>
        {
            store.AddTable(new RateLimitTable(schemaName, options.TableName));
        });

        concrete.Options.Services.TryAddSingleton(options);
        concrete.Options.Services.TryAddSingleton<IRateLimitStore, SqlServerRateLimitStore>();

        return persistence;
    }

    /// <summary>
    /// Register Sql Server backed message persistence *and* the Sql Server messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema">Schema name for the Wolverine envelope storage. A single database identifier, not a multi-part name</param>
    /// <returns></returns>
    public static SqlServerPersistenceExpression UseSqlServerPersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null,
        string? transportSchema = null,
        MessageStoreRole role = MessageStoreRole.Main)
    {
        // GH-3226: forward the message-store role so apps that already have an event-store-backed Main
        // (Marten / Polecat) can register this transport's persistence as Ancillary instead of a second Main.
        options.PersistMessagesWithSqlServer(connectionString, schema, role);

        options.Services.AddTransient<IDatabase, SqlServerTransportDatabase>();

        var transport = new SqlServerTransport(new DatabaseSettings
        {
            ConnectionString = connectionString,
            SchemaName = schema ?? "dbo"
        }, transportSchema);
        
        options.Transports.Add(transport);

        return new SqlServerPersistenceExpression(transport, options);
    }

    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static SqlServerTransport SqlServerTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        try
        {
            return transports.OfType<SqlServerTransport>().Single();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The Sql Server transport is not registered in this system");
        }
    }

    /// <summary>
    /// Listen for incoming messages at the designated Sql Server queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static SqlServerListenerConfiguration ListenToSqlServerQueue(this WolverineOptions endpoints, string queueName)
    {
        var transport = endpoints.SqlServerTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new SqlServerListenerConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages straight to a Sql Server queue using the queue name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static SqlServerSubscriberConfiguration ToSqlServerQueue(this IPublishToExpression publishing,
        string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.OfType<SqlServerTransport>().Single();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new SqlServerSubscriberConfiguration(queue);
    }

    /// <summary>
    /// Shard message publishing across a set of SQL Server queues named baseName1, baseName2, and so on
    /// using the global message grouping rules. This is the publishing-only variant -- see
    /// UseShardedSqlServerQueues() for the full global partitioning topology that also shards the
    /// message *execution* across companion local queues.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedSqlServerQueues(this MessagePartitioningRules rules,
        string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithDatabaseQueues> configure)
    {
        rules.AddPublishingTopology((opts, _) =>
        {
            var topology =
                new PartitionedMessageTopologyWithDatabaseQueues(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            topology.ConfigureListening(x => { });
            configure(topology);
            topology.AssertValidity();

            return topology;
        });

        return rules;
    }

    /// <summary>
    /// Use sharded SQL Server queues for global partitioned message processing.
    /// Queues will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedSqlServerQueues(
        this GlobalPartitionedMessageTopology topology, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithDatabaseQueues>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithDatabaseQueues(opts, PartitionSlots.Five, baseName,
                numberOfEndpoints);
            t.ConfigureListening(x => { });
            configure?.Invoke(t);
            return t;
        }, baseName);

        return topology;
    }
}