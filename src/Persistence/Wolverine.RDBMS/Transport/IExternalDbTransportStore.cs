using System.Data.Common;
using Weasel.Core;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Transport;

/// <summary>
/// A transport store that interacts with external database tables for message transport
/// </summary>
public interface IExternalDbTransportStore
{
    /// <summary>
    /// The underlying database connection for this transport store
    /// </summary>
    DbDataSource DataSource { get; }

    /// <summary>
    /// Polls for messages from external tables
    /// </summary>
    /// <param name="listener">The listener to receive messages</param>
    /// <param name="settings">The Wolverine runtime settings</param>
    /// <param name="externalTable">The external message table to poll for messages</param>
    /// <param name="receiver">The receiver to handle the messages</param>
    /// <param name="token">A cancellation token</param>
    Task PollForMessagesFromExternalTablesAsync(IListener listener,
        IWolverineRuntime settings, ExternalMessageTable externalTable,
        IReceiver receiver,
        CancellationToken token);

    /// <summary>
    /// Creates or updates the external message table in the database if it does not already exist
    /// </summary>
    /// <param name="messageTable">The external message table to migrate</param>
    Task MigrateExternalMessageTable(ExternalMessageTable messageTable);

    /// <summary>
    /// Publishes a message to an external table
    /// </summary>
    /// <param name="table">The external message table to publish the message to</param>
    /// <param name="messageTypeName">The name of the message type. Only required if the table has a MessageType column</param>
    /// <param name="json">The message content in JSON format</param>
    /// <param name="token">A cancellation token</param>
    Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string? messageTypeName, byte[] json, CancellationToken token);

    /// <summary>
    /// Builds a ITable object for the given external message table definition
    /// </summary>
    /// <param name="definition">The external message table definition</param>
    /// <returns>The built ITable object</returns>
    ITable AddExternalMessageTable(ExternalMessageTable definition);
}
