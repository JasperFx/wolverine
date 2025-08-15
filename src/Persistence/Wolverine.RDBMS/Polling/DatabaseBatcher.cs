using System.Threading.Tasks.Dataflow;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.RDBMS.Polling;

public class DatabaseBatcher : IAsyncDisposable
{
    private readonly IBlock<IDatabaseOperation> _batchingBlock;
    private readonly IMessageDatabase _database;
    private readonly Lazy<IExecutor> _executor;
    private readonly ILogger<DatabaseBatcher> _logger;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationTokenSource _internalCancellation;

    public DatabaseBatcher(IMessageDatabase database, IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        _database = database;
        _runtime = runtime;

        _internalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var executingBlock = new Block<IDatabaseOperation[]>(processOperationsAsync);
        _batchingBlock = executingBlock.BatchUpstream(250.Milliseconds(), 100);

        _logger = _runtime.LoggerFactory.CreateLogger<DatabaseBatcher>();

        _executor = new Lazy<IExecutor>(() => runtime.As<IExecutorFactory>().BuildFor(typeof(DatabaseOperationBatch)));
    }

    public ValueTask DisposeAsync()
    {
        return _batchingBlock.DisposeAsync();

    }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        return _batchingBlock.PostAsync(operation).AsTask();
    }

    public void Enqueue(IDatabaseOperation operation)
    {
        _batchingBlock.Post(operation);
    }

    private async Task processOperationsAsync(IDatabaseOperation[] operations, CancellationToken _)
    {
        if (_internalCancellation.Token.IsCancellationRequested) return;

        try
        {
            await _executor.Value.InvokeAsync(new DatabaseOperationBatch(_database, operations),
                new MessageBus(_runtime), _internalCancellation.Token);
        }
        catch (Exception e)
        {
            if (_internalCancellation.Token.IsCancellationRequested) return;

            _logger.LogError(e, "Error running database operations {Operations} against message database {Database}",
                operations.Select(x => x.Description).Join(", "), _database);
        }
    }

    public async Task DrainAsync()
    {
        try
        {
            await _internalCancellation.CancelAsync();

            await _batchingBlock.WaitForCompletionAsync();
        }
        catch (TaskCanceledException)
        {
            // it just timed out, let it go
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to drain the current database batcher");
        }
    }
}