using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.Sqlite;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite.Transport;

namespace Wolverine.Sqlite;

public static class SqliteConfigurationExtensions
{
    /// <summary>
    ///     Register SQLite backed message persistence to a known connection string
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static ISqliteBackedPersistence PersistMessagesWithSqlite(this WolverineOptions options, string connectionString,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
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
    /// <param name="dataSource"></param>
    /// <param name="schemaName">Optional schema name for the Wolverine envelope storage</param>
    /// <param name="role">Default is Main. Use this to mark some stores as Ancillary to disambiguate the main storage for Wolverine</param>
    public static ISqliteBackedPersistence PersistMessagesWithSqlite(this WolverineOptions options, DbDataSource dataSource,
        string? schemaName = null, MessageStoreRole role = MessageStoreRole.Main)
    {
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

    /// <summary>
    /// Register SQLite backed message persistence *and* the SQLite messaging transport
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionString"></param>
    /// <param name="schema"></param>
    /// <param name="transportSchema"></param>
    /// <returns></returns>
    public static SqlitePersistenceExpression UseSqlitePersistenceAndTransport(this WolverineOptions options,
        string connectionString,
        string? schema = null,
        string? transportSchema = "wolverine_queues")
    {
        options.PersistMessagesWithSqlite(connectionString, schema);

        var transport = options.Transports.GetOrCreate<SqliteTransport>();
        transport.MessageStorageSchemaName = schema ?? "main";

        if (transportSchema.IsNotEmpty())
        {
            transport.TransportSchemaName = transportSchema;
        }

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
