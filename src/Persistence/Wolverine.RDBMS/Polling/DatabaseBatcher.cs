using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Polling;

public class DatabaseBatcher : IAsyncDisposable
{
    private readonly ActionBlock<IDatabaseOperation[]> _executor;
    private readonly BatchingBlock<IDatabaseOperation> _batchingBlock;
    private readonly IMessageDatabase _database;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationToken _cancellationToken;
    private readonly ILogger<DatabaseBatcher> _logger;

    public DatabaseBatcher(IMessageDatabase database, IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        _database = database;
        _runtime = runtime;
        _cancellationToken = cancellationToken;
        _executor = new ActionBlock<IDatabaseOperation[]>(processOperationsAsync, new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1
        });

        _batchingBlock = new BatchingBlock<IDatabaseOperation>(250 ,_executor, cancellationToken);

        _logger = _runtime.LoggerFactory.CreateLogger<DatabaseBatcher>();
    }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        return _batchingBlock.SendAsync(operation);
    }

    private async Task processOperationsAsync(IDatabaseOperation[] operations)
    {
        try
        {
            await new MessageBus(_runtime).InvokeAsync(new DatabaseOperationBatch(_database, operations), _cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError("Error running database operations {Operations} against message database {Database}", operations.Select(x => x.Description).Join(", "), _database);
        }
    }


    public ValueTask DisposeAsync()
    {
        _batchingBlock.Dispose();
        
        return ValueTask.CompletedTask;
    }

    public async Task DrainAsync()
    {
        _batchingBlock.Complete();
        await _batchingBlock.Completion;

        _executor.Complete();
        await _executor.Completion;
    }
}
