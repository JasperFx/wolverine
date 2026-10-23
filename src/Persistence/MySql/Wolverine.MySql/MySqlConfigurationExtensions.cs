using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Wolverine.Configuration;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.MySql;

public static class MySqlConfigurationExtensions
{
    // MySQL schema name validation
    internal static void AssertValidSchemaName(this string schemaName)
    {
        if (schemaName.IsEmpty())
            throw new ArgumentNullException(nameof(schemaName), "Schema Name cannot be empty or null");

        // MySQL identifiers are case-insensitive on Windows but case-sensitive on Linux
        // For consistency, we recommend lowercase
        if (schemaName.Contains("-"))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "MySQL schema names cannot include dashes. Use underscores instead");
        }
    }

    /// <summary>
    ///     Register MySQL backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IMySqlBackedPersistence PersistMessagesWithMySql(this WolverineOptions options,
        string connectionString,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new MySqlBackedPersistence(options.Durability, options)
        {
            ConnectionString = connectionString,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Register MySQL backed message persistence to a known MySqlDataSource
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSource">Owned by the caller. Wolverine does not dispose it, so it can be shared with the rest of the application</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IMySqlBackedPersistence PersistMessagesWithMySql(this WolverineOptions options,
        MySqlDataSource dataSource,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new MySqlBackedPersistence(options.Durability, options)
        {
            DataSource = dataSource,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.Include(persistence);

        persistence.AlreadyIncluded = true;

        return persistence;
    }

    internal static NamedConfigurationDependency NamedConnectionDependency(string connectionStringName) =>
        new("MySQL", connectionStringName, "AddMySql") { Usage = "message store" };

    /// <summary>
    ///     Register MySQL backed message persistence to the named connection string from IConfiguration. This
    ///     option is useful for Aspire integration, where the AppHost's <c>builder.AddMySql("server").AddDatabase("name")</c>
    ///     plus <c>.WithReference(...)</c> supplies <c>ConnectionStrings:name</c>. The value is resolved once the
    ///     application's container exists, before the message store is built; a missing name fails startup together
    ///     with every other missing named connection string.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IMySqlBackedPersistence PersistMessagesWithMySqlUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var dependency = NamedConnectionDependency(connectionStringName);
        var persistence = new MySqlBackedPersistence(options.Durability, options)
        {
            NamedConnection = dependency,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.ApplyNamedConnectionString(dependency, value => persistence.ConnectionString = value);

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Register MySQL backed message persistence to a <see cref="MySqlDataSource"/> registered in the
    ///     application's container -- e.g. by Aspire's <c>builder.AddMySqlDataSource("name")</c> client integration
    ///     (Aspire.MySqlConnector). The data source is resolved when the message store is built, and is owned by the
    ///     container: Wolverine does not dispose it.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSourceSource">How to find the data source. Defaults to resolving <see cref="MySqlDataSource"/>; pass e.g. <c>s => s.GetRequiredKeyedService&lt;MySqlDataSource&gt;("name")</c> for a keyed registration (Aspire's AddKeyedMySqlDataSource)</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static IMySqlBackedPersistence PersistMessagesWithMySqlUsingDataSourceFromContainer(
        this WolverineOptions options, Func<IServiceProvider, MySqlDataSource>? dataSourceSource = null,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        var persistence = new MySqlBackedPersistence(options.Durability, options)
        {
            DataSourceSource = dataSourceSource ?? (s => s.GetRequiredService<MySqlDataSource>()),
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
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
    public static IMySqlBackedPersistence RegisterStaticTenantsUsingNamedConnections(
        this IMySqlBackedPersistence persistence, Action<NamedConnectionStringTenants> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var tenants = new NamedConnectionStringTenants();
        configure(tenants);

        StaticConnectionStringSource source = null!;
        persistence.RegisterStaticTenants(x => source = x);

        tenants.ApplyTo(((MySqlBackedPersistence)persistence).Options, source, "MySQL", "AddMySql");

        return persistence;
    }

    /// <summary>
    /// Register MySQL backed message persistence *and* the MySQL messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema">Schema name for the Wolverine envelope storage. A single database identifier, not a multi-part name</param>
    /// <param name="transportSchema">Schema name for the queue tables. A single database identifier, not a multi-part name</param>
    /// <returns></returns>
    public static MySqlPersistenceExpression UseMySqlPersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null,
        string? transportSchema = "wolverine_queues")
    {
        options.PersistMessagesWithMySql(connectionString, schema);

        if (transportSchema != null)
        {
            transportSchema.AssertValidSchemaName();
        }

        var transport = options.Transports.GetOrCreate<MySqlTransport>();
        transport.MessageStorageSchemaName = schema ?? "wolverine";

        if (transportSchema.IsNotEmpty())
        {
            transport.TransportSchemaName = transportSchema;
        }

        options.Transports.Add(transport);

        return new MySqlPersistenceExpression(transport, options);
    }

    /// <summary>
    ///     Quick access to the MySQL Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static MySqlTransport MySqlTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        try
        {
            return transports.GetOrCreate<MySqlTransport>();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The MySQL transport is not registered in this system");
        }
    }

    /// <summary>
    /// Listen for incoming messages at the designated MySQL queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static MySqlListenerConfiguration ListenToMySqlQueue(this WolverineOptions endpoints, string queueName)
    {
        var transport = endpoints.MySqlTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new MySqlListenerConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages straight to a MySQL queue using the queue name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static MySqlSubscriberConfiguration ToMySqlQueue(this IPublishToExpression publishing,
        string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<MySqlTransport>();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new MySqlSubscriberConfiguration(queue);
    }
}
