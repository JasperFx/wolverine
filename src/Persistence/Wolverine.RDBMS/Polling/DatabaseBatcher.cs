using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Polling;

public class DatabaseBatcher : IAsyncDisposable
{
    private readonly BatchingBlock<IDatabaseOperation> _batchingBlock;
    private readonly IMessageDatabase _database;
    private readonly ActionBlock<IDatabaseOperation[]> _executingBlock;
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

        _executingBlock = new ActionBlock<IDatabaseOperation[]>(processOperationsAsync,
            new ExecutionDataflowBlockOptions
            {
                EnsureOrdered = true,
                MaxDegreeOfParallelism = 1,
                CancellationToken = _internalCancellation.Token
            });

        _batchingBlock = new BatchingBlock<IDatabaseOperation>(250, _executingBlock, _internalCancellation.Token);

        _logger = _runtime.LoggerFactory.CreateLogger<DatabaseBatcher>();

        _executor = new Lazy<IExecutor>(() => runtime.As<IExecutorFactory>().BuildFor(typeof(DatabaseOperationBatch)));
    }

    public ValueTask DisposeAsync()
    {
        _batchingBlock.Dispose();

        return ValueTask.CompletedTask;
    }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        return _batchingBlock.SendAsync(operation);
    }

    public void Enqueue(IDatabaseOperation operation)
    {
        _batchingBlock.Send(operation);
    }

    private async Task processOperationsAsync(IDatabaseOperation[] operations)
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
        _internalCancellation.Cancel();

        _batchingBlock.Complete();
        await _batchingBlock.Completion;

        _executingBlock.Complete();
        await _executingBlock.Completion;
    }
}