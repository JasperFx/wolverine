using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.RDBMS.Transport;

public static class ExternalDbTransportExtensions
{
    /// <summary>
    /// Testing helper to publish a message to an externally controlled message table
    /// </summary>
    /// <param name="host"></param>
    /// <param name="qualifiedTableName"></param>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public static Task SendMessageThroughExternalTable(this IHost host, string qualifiedTableName, object message,
        CancellationToken token = default)
    {
        return host.Services.GetRequiredService<IWolverineRuntime>()
            .SendMessageThroughExternalTable(qualifiedTableName, message, token);
    }
    
    /// <summary>
    /// Testing helper to publish a message to an externally controlled message table
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="qualifiedTableName"></param>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static async Task SendMessageThroughExternalTable(this IWolverineRuntime runtime, string qualifiedTableName,
        object message, CancellationToken token = default)
    {
        if (qualifiedTableName == null)
        {
            throw new ArgumentNullException(nameof(qualifiedTableName));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var serializer = runtime.Options.FindSerializer("application/json");
        var json = serializer.WriteMessage(message);
        var database = runtime.Storage.As<IMessageDatabase>();
        var messageTypeName = message.GetType().ToMessageTypeName();

        var transport = runtime.Options.ExternalDbTransport();
        var table = transport.Tables.FirstOrDefault(x =>
            x.TableName.QualifiedName.EqualsIgnoreCase(qualifiedTableName));

        if (table == null)
        {
            throw new ArgumentOutOfRangeException(nameof(qualifiedTableName), $"Unknown external table '{qualifiedTableName}'");
        }

        if (table.AllowWolverineControl)
        {
            await database.MigrateExternalMessageTable(table);
        }

        await database.PublishMessageToExternalTableAsync(table, messageTypeName, json, token);
    }
    
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static ExternalDbTransport ExternalDbTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<ExternalDbTransport>();
    }

    /// <summary>
    /// Register a message listener to an external database table that conforms to Wolverine's specification
    /// for incoming messages
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="schemaName">The database schema name for the external table</param>
    /// <param name="tableName">The database table name for the external table</param>
    /// <param name="configure">Optional configuration of the external table for Wolverine's polling of this table</param>
    /// <returns></returns>
    public static ListenerConfiguration ListenForMessagesFromExternalDatabaseTable(this WolverineOptions endpoints, string schemaName, string tableName,
        Action<IExternalMessageTable>? configure = null)
    {
        var transport = endpoints.ExternalDbTransport();
        var table = transport.Tables[new DbObjectName(schemaName, tableName)];
        configure?.Invoke(table);

        return new ListenerConfiguration(table);
    }
}