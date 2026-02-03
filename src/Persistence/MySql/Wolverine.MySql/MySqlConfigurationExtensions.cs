using JasperFx.Core;
using JasperFx.Core.Reflection;
using MySqlConnector;
using Wolverine.Configuration;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;

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
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
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
    /// <param name="dataSource"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
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

    /// <summary>
    /// Register MySQL backed message persistence *and* the MySQL messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    /// <param name="transportSchema"></param>
    /// <returns></returns>
    [Obsolete("Prefer PersistMessagesWithMySql().EnableMessageTransport()")]
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
