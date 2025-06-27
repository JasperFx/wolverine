using JasperFx.Core;
using Npgsql;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Postgresql.Transport;


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

        if (schemaName.Contains("-"))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "PostgreSQL schema names cannot include dashes. Use underscores instead");
        }
    }
    
    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresql(this WolverineOptions options, string connectionString,
        string? schemaName = null)
    {
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            ConnectionString = connectionString,
            AlreadyIncluded = true
        };

        if (schemaName.IsNotEmpty())
        {
            schemaName.AssertValidSchemaName();
            persistence.EnvelopeStorageSchemaName = schemaName;
        }

        options.Include(persistence);

        return persistence;
    }

    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSource"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    public static IPostgresqlBackedPersistence PersistMessagesWithPostgresql(this WolverineOptions options, NpgsqlDataSource dataSource,
        string? schemaName = null)
    {
        var persistence = new PostgresqlBackedPersistence(options.Durability, options)
        {
            DataSource = dataSource
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
    /// Register PostgreSQL backed message persistence *and* the PostgreSQL messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    /// <returns></returns>
    [Obsolete("Prefer PersistMessagesWithPostgresql().EnableMessageTransport()")]
    public static PostgresqlPersistenceExpression UsePostgresqlPersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null,
        string? transportSchema = "wolverine_queues")
    {
        options.PersistMessagesWithPostgresql(connectionString, schema);

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
}