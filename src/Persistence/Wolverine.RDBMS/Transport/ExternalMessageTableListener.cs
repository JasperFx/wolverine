using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Transport;

internal class ExternalMessageTableListener : IListener
{
    private readonly ExternalMessageTable _messageTable;
    private readonly IMessageDatabase _database;
    private readonly WolverineOptions _runtimeOptions;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _task;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<ExternalMessageTableListener> _logger;

    public ExternalMessageTableListener(ExternalMessageTable messageTable, IWolverineRuntime runtime, IReceiver receiver)
    {
        var database = runtime.Storage as IMessageDatabase;

        _messageTable = messageTable;
        _database = database ?? throw new InvalidOperationException(
            "The external table transport option can only be used in combination with a relational database message storage option, but the message store is " +
            runtime.Storage);
        _runtimeOptions = runtime.Options;
        _runtime = runtime;

        _logger = runtime.LoggerFactory.CreateLogger<ExternalMessageTableListener>();

        Address = messageTable.Uri;

        if (receiver is DurableReceiver durable)
        {
            durable.ShouldPersistBeforeProcessing = false;
        }
        else if (receiver is ReceiverWithRules { Inner: DurableReceiver inner })
        {
            inner.ShouldPersistBeforeProcessing = false;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_runtimeOptions.Durability.Cancellation);

        _task = Task.Run(async () =>
        {
            // Wait a random amount to try to avoid contesting for the shared lock
            await Task.Delay(Random.Shared.Next(0, 2000), _cancellation.Token);

            if (_runtimeOptions.AutoBuildMessageStorageOnStartup && _messageTable.AllowWolverineControl)
            {
                try
                {
                    await _database.MigrateExternalMessageTable(_messageTable);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to migrate external message table {Table}", _messageTable.TableName.QualifiedName);
                }
            }
            
            while (!_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    await _database.PollForMessagesFromExternalTablesAsync(this, _runtime, _messageTable,
                        receiver, _cancellation.Token);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to poll for messages from external table {Table}", _messageTable.TableName.QualifiedName);
                }

                await Task.Delay(_messageTable.PollingInterval, _cancellation.Token);
            }
        }, _cancellation.Token);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();

        _task.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address { get; }
    public ValueTask StopAsync()
    {
        return DisposeAsync();
    }
}