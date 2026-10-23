using JasperFx.Core;
using Npgsql;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime.Partitioning;


namespace Wolverine.Postgresql;

public static class PostgresqlConfigurationExtensions
{
    // TODO -- this should be in Weasel.Postgresql
    internal static void AssertValidSchemaName(this string schemaName)
    {
        if (schemaName.IsEmpty())
            throw new ArgumentNullException(nameof(schemaName), "Schema Name cannot be empty or null");

        if (schemaName.IsNotEmpty() && schemaName != schemaName.ToLowerInvariant())
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "The schema name must be in all lower case characters");
        }
    }

    /// <summary>
    /// Quotes a PostgreSQL identifier (schema, table, column name) with double quotes.
    /// Internal double quotes are escaped by doubling them.
    /// </summary>
    internal static string QuoteIdentifier(this string? identifier)
    {
        if (identifier.IsEmpty()) return identifier ?? string.Empty;

        // Escape any internal double quotes by doubling them
        var escaped = identifier.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
    
    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresql(this WolverineOptions options, string connectionString,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            ConnectionString = connectionString,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            schemaName.AssertValidSchemaName();
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSource">Owned by the caller. Wolverine does not dispose it, so it can be shared with the rest of the application</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresql(this WolverineOptions options, NpgsqlDataSource dataSource,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            DataSource = dataSource,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            schemaName.AssertValidSchemaName();
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.Include(persistence);

        persistence.AlreadyIncluded = true;

        return persistence;
    }

    internal static NamedConfigurationDependency NamedConnectionDependency(string connectionStringName) =>
        new("PostgreSQL", connectionStringName, "AddPostgres") { Usage = "message store" };

    /// <summary>
    ///     Register PostgreSQL backed message persistence to the named connection string from IConfiguration. This
    ///     option is useful for Aspire integration, where the AppHost's <c>builder.AddPostgres("server").AddDatabase("name")</c>
    ///     plus <c>.WithReference(...)</c> supplies <c>ConnectionStrings:name</c>. The value is resolved once the
    ///     application's container exists, before the message store is built; a missing name fails startup together
    ///     with every other missing named connection string.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresqlUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var dependency = NamedConnectionDependency(connectionStringName);
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            NamedConnection = dependency,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            schemaName.AssertValidSchemaName();
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.ApplyNamedConnectionString(dependency, value => persistence.ConnectionString = value);

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Register PostgreSQL backed message persistence to an <see cref="NpgsqlDataSource"/> registered in the
    ///     application's container -- e.g. by Aspire's <c>builder.AddNpgsqlDataSource("name")</c> client integration.
    ///     The data source is resolved when the message store is built, and is owned by the container: Wolverine does
    ///     not dispose it.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSourceSource">How to find the data source. Defaults to resolving <see cref="NpgsqlDataSource"/>; pass e.g. <c>s => s.GetRequiredKeyedService&lt;NpgsqlDataSource&gt;("name")</c> for a keyed registration</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresqlUsingDataSourceFromContainer(
        this WolverineOptions options, Func<IServiceProvider, NpgsqlDataSource>? dataSourceSource = null,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            DataSourceSource = dataSourceSource ?? (s => s.GetRequiredService<NpgsqlDataSource>()),
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            schemaName.AssertValidSchemaName();
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.Include(persistence);

        persistence.AlreadyIncluded = true;

        return persistence;
    }

    /// <summary>
    ///     Opt into static per-tenant database multi-tenancy where each tenant database's connection string is
    ///     resolved by name from IConfiguration -- e.g. one Aspire database resource per tenant, each referenced
    ///     from this project. Every name joins the aggregated startup check.
    /// </summary>
    /// <param name="persistence"></param>
    /// <param name="configure">Register each tenant id and the name of its connection string</param>
    public static IPostgresqlBackedPersistence RegisterStaticTenantsUsingNamedConnections(
        this IPostgresqlBackedPersistence persistence, Action<NamedConnectionStringTenants> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var tenants = new NamedConnectionStringTenants();
        configure(tenants);

        StaticConnectionStringSource source = null!;
        persistence.RegisterStaticTenants(x => source = x);

        tenants.ApplyTo(((PostgresqlBackedPersistence)persistence).Options, source, "PostgreSQL", "AddPostgres");

        return persistence;
    }

    /// <summary>
    /// Register PostgreSQL backed message persistence *and* the PostgreSQL messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema">Schema name for the Wolverine envelope storage. A single database identifier, not a multi-part name</param>
    /// <returns></returns>
    public static PostgresqlPersistenceExpression UsePostgresqlPersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null,
        string? transportSchema = "wolverine_queues",
        MessageStoreRole role = MessageStoreRole.Main)
    {
        // GH-3226: forward the message-store role so apps that already have an event-store-backed Main
        // (Marten / Polecat) can register this transport's persistence as Ancillary instead of a second Main.
        options.PersistMessagesWithPostgresql(connectionString, schema, role);

        if (transportSchema != null)
        {
            transportSchema.AssertValidSchemaName();
        }
        
        var transport = options.Transports.GetOrCreate<PostgresqlTransport>();
        transport.MessageStorageSchemaName = schema ?? "public";

        if (transportSchema.IsNotEmpty())
        {
            transport.TransportSchemaName = transportSchema;
        }

        options.Transports.Add(transport);

        return new PostgresqlPersistenceExpression(transport, options);
    }

    /// <summary>
    ///     Quick access to the Postgresql Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PostgresqlTransport PostgresqlTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        try
        {
            return transports.GetOrCreate<PostgresqlTransport>();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The PostgreSQL transport is not registered in this system");
        }
    }

    /// <summary>
    /// Listen for incoming messages at the designated PostgreSQL queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static PostgresqlListenerConfiguration ListenToPostgresqlQueue(this WolverineOptions endpoints, string queueName)
    {
        var transport = endpoints.PostgresqlTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new PostgresqlListenerConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages straight to a PostgreSQL queue using the queue name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static PostgresqlSubscriberConfiguration ToPostgresqlQueue(this IPublishToExpression publishing,
        string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PostgresqlTransport>();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new PostgresqlSubscriberConfiguration(queue);
    }

    /// <summary>
    /// Shard message publishing across a set of PostgreSQL queues named baseName1, baseName2, and so on
    /// using the global message grouping rules. This is the publishing-only variant -- see
    /// UseShardedPostgresqlQueues() for the full global partitioning topology that also shards the
    /// message *execution* across companion local queues.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedPostgresqlQueues(this MessagePartitioningRules rules,
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
    /// Use sharded PostgreSQL queues for global partitioned message processing.
    /// Queues will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedPostgresqlQueues(
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