using JasperFx.Core;
using Npgsql;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;


namespace Wolverine.Postgresql;

public static class PostgresqlConfigurationExtensions
{
    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    public static void PersistMessagesWithPostgresql(this WolverineOptions options, string connectionString,
        string? schemaName = null)
    {
        if (schemaName.IsNotEmpty() && schemaName != schemaName.ToLowerInvariant())
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "The schema name must be in all lower case characters");
        }
        
        options.Include<PostgresqlBackedPersistence>(o =>
        {
            o.Settings.ConnectionString = connectionString;
            o.Settings.SchemaName = schemaName ?? "public";
            o.Settings.DataSource = NpgsqlDataSource.Create(connectionString);
            
            o.Settings.ScheduledJobLockId = $"{schemaName ?? "public"}:scheduled-jobs".GetDeterministicHashCode();
        });
    }
    
    /// <summary>
    ///     Register Postgresql backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="dataSource"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    public static void PersistMessagesWithPostgresql(this WolverineOptions options, NpgsqlDataSource dataSource,
        string? schemaName = null)
    {
        if (schemaName.IsNotEmpty() && schemaName != schemaName.ToLowerInvariant())
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "The schema name must be in all lower case characters");
        }
        
        options.Include<PostgresqlBackedPersistence>(o =>
        {
            o.Settings.SchemaName = schemaName ?? "public";
            o.Settings.DataSource = dataSource;
            
            o.Settings.ScheduledJobLockId = $"{schemaName ?? "public"}:scheduled-jobs".GetDeterministicHashCode();
        });
    }
    
    /// <summary>
    /// Register PostgreSQL backed message persistence *and* the PostgreSQL messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    /// <returns></returns>
    public static PostgresqlPersistenceExpression UsePostgresqlPersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null)
    {
        var extension = new PostgresqlBackedPersistence
        {
            Settings =
            {
                ConnectionString = connectionString
            }
        };

        if (schema.IsNotEmpty())
        {
            extension.Settings.SchemaName = schema;
        }
        else
        {
            schema = "dbo";
                
        }

        extension.Settings.ScheduledJobLockId = $"{schema}:scheduled-jobs".GetDeterministicHashCode();
        options.Include(extension);
        
        options.Include<PostgresqlBackedPersistence>(x =>
        {
            x.Settings.ConnectionString = connectionString;

            if (schema.IsNotEmpty())
            {
                x.Settings.SchemaName = schema;
            }
            else
            {
                schema = "dbo";
                
            }

            x.Settings.ScheduledJobLockId = $"{schema}:scheduled-jobs".GetDeterministicHashCode();
        });

        var transport = options.Transports.GetOrCreate<PostgresqlTransport>();
        if (schema.IsNotEmpty())
        {
            transport.SchemaName = schema;
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