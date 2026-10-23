using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Sqlite.Transport;

namespace Wolverine.Sqlite;

public static class SqliteConfigurationExtensions
{
    /// <summary>
    ///     Register SQLite backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static ISqliteBackedPersistence PersistMessagesWithSqlite(this WolverineOptions options, string connectionString,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        SqliteConnectionStringPolicy.AssertFileBased(connectionString, nameof(connectionString));

        var persistence = new SqliteBackedPersistence(options.Durability, options)
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
    ///     Register SQLite backed message persistence with a DbDataSource
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSource">Owned by the caller. Wolverine does not dispose it, so it can be shared with the rest of the application</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static ISqliteBackedPersistence PersistMessagesWithSqlite(this WolverineOptions options, DbDataSource dataSource,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        SqliteConnectionStringPolicy.AssertFileBased(dataSource, nameof(dataSource));

        var persistence = new SqliteBackedPersistence(options.Durability, options)
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
        new("SQLite", connectionStringName, "AddSqlite") { Usage = "message store" };

    /// <summary>
    ///     Register SQLite backed message persistence to the named connection string from IConfiguration. This
    ///     option is useful for Aspire integration, where the AppHost's <c>builder.AddSqlite("name")</c> (from
    ///     CommunityToolkit.Aspire.Hosting.Sqlite) plus <c>.WithReference(...)</c> supplies <c>ConnectionStrings:name</c>.
    ///     The value is resolved once the application's container exists, before the message store is built; a
    ///     missing name fails startup together with every other missing named connection string. The resolved value
    ///     must still point to a file-based database.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage. This has to be a single database identifier; a multi-part name like "crm.sales" is rejected. See GH-3997</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static ISqliteBackedPersistence PersistMessagesWithSqliteUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var dependency = NamedConnectionDependency(connectionStringName);
        var persistence = new SqliteBackedPersistence(options.Durability, options)
        {
            NamedConnection = dependency,
            AlreadyIncluded = true,
            Role = role
        };

        if (schemaName.IsNotEmpty())
        {
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.ApplyNamedConnectionString(dependency, value =>
        {
            SqliteConnectionStringPolicy.AssertFileBased(value, $"connection string '{connectionStringName}'");
            persistence.ConnectionString = value;
        });

        persistence.Configure(options);

        return persistence;
    }

    /// <summary>
    ///     Opt into static per-tenant database multi-tenancy with separate SQLite files where each tenant's
    ///     connection string is resolved by name from IConfiguration -- e.g. one Aspire <c>AddSqlite(...)</c>
    ///     resource per tenant, each referenced from this project. Every name joins the aggregated startup check,
    ///     and every resolved value must point to a file-based database.
    /// </summary>
    /// <param name="persistence"></param>
    /// <param name="configure">Register each tenant id and the name of its connection string</param>
    public static ISqliteBackedPersistence RegisterStaticTenantsUsingNamedConnections(
        this ISqliteBackedPersistence persistence, Action<NamedConnectionStringTenants> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var tenants = new NamedConnectionStringTenants();
        configure(tenants);

        StaticConnectionStringSource source = null!;
        persistence.RegisterStaticTenants(x => source = x);

        var options = ((SqliteBackedPersistence)persistence).Options;
        foreach (var (tenantId, connectionStringName) in tenants.Tenants)
        {
            var dependency = new NamedConfigurationDependency("SQLite", connectionStringName, "AddSqlite")
            {
                Usage = $"message store (tenant '{tenantId}')"
            };

            // Not NamedConnectionStringTenants.ApplyTo(): the SQLite file-based rule that RegisterStaticTenants()
            // applies at configuration time has to be applied here, once the value is known
            options.ApplyNamedConnectionString(dependency, value =>
            {
                SqliteConnectionStringPolicy.AssertFileBased(value, $"tenant '{tenantId}'");
                source.Register(tenantId, value);
            });
        }

        return persistence;
    }

    /// <summary>
    /// Register SQLite backed message persistence *and* the SQLite messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <returns></returns>
    public static SqlitePersistenceExpression UseSqlitePersistenceAndTransport(this WolverineOptions options,
        string connectionString)
    {
        options.PersistMessagesWithSqlite(connectionString);

        var transport = options.Transports.GetOrCreate<SqliteTransport>();

        options.Transports.Add(transport);

        return new SqlitePersistenceExpression(transport, options);
    }

    /// <summary>
    ///     Quick access to the SQLite Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static SqliteTransport SqliteTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        try
        {
            return transports.GetOrCreate<SqliteTransport>();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The SQLite transport is not registered in this system");
        }
    }

    /// <summary>
    /// Listen for incoming messages at the designated SQLite queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static SqliteListenerConfiguration ListenToSqliteQueue(this WolverineOptions endpoints, string queueName)
    {
        var transport = endpoints.SqliteTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        return new SqliteListenerConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages straight to a SQLite queue using the queue name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static SqliteSubscriberConfiguration ToSqliteQueue(this IPublishToExpression publishing,
        string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SqliteTransport>();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new SqliteSubscriberConfiguration(queue);
    }
}
