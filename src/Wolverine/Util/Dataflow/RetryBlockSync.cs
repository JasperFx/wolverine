using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks.Dataflow;

namespace Wolverine.Util.Dataflow;

public class RetryBlockSync<T> : IRetryBlock<T>
{
    private readonly ActionBlock<Item> _block;
    private readonly CancellationToken _cancellationToken;
    private readonly IItemHandler<T> _handler;
    private readonly ILogger _logger;

    public RetryBlockSync(Func<T, CancellationToken, Task> handler, ILogger logger, CancellationToken cancellationToken,
        Action<ExecutionDataflowBlockOptions>? configure = null)
        : this(new LambdaItemHandler<T>(handler), logger, cancellationToken, configure)
    {
    }

    public RetryBlockSync(Func<T, CancellationToken, Task> handler, ILogger logger, CancellationToken cancellationToken,
        ExecutionDataflowBlockOptions options)
    {
        _handler = new LambdaItemHandler<T>(handler);
        _logger = logger;

        options.CancellationToken = cancellationToken;
        options.SingleProducerConstrained = true;

        _cancellationToken = cancellationToken;

        _block = new ActionBlock<Item>(executeAsync, options);
    }

    public RetryBlockSync(IItemHandler<T> handler, ILogger logger, CancellationToken cancellationToken,
        Action<ExecutionDataflowBlockOptions>? configure = null)
    {
        _handler = handler;
        _logger = logger;
        var options = new ExecutionDataflowBlockOptions
        {
            CancellationToken = cancellationToken,
            SingleProducerConstrained = true
        };

        configure?.Invoke(options);

        _cancellationToken = cancellationToken;

        _block = new ActionBlock<Item>(executeAsync, options);
    }

    public int MaximumAttempts { get; set; } = 4;
    public TimeSpan[] Pauses { get; set; } = [50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds()];

    public void Dispose()
    {
        _block.Complete();
    }

    public void Post(T message)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        var item = new Item(message);
        _block.Post(item);
    }

    public Task PostAsync(T message)
    {
        if (_cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        Post(message);
        
        return _block.Completion;
    }

    public TimeSpan DeterminePauseTime(int attempt)
    {
        if (attempt >= Pauses.Length)
        {
            return Pauses.LastOrDefault();
        }

        return Pauses[attempt - 1];
    }

    private async Task executeAsync(Item item)
    {
        if (_cancellationToken.IsCancellationRequested) return;

        try
        {
            item.Attempts++;

            if (item.Attempts > 1)
            {
                var pause = DeterminePauseTime(item.Attempts);
                await Task.Delay(pause, _cancellationToken);
            }
            await _handler.ExecuteAsync(item.Message, _cancellationToken);
            _logger.LogDebug("Completed {Item}", item.Message);

            _block.Complete();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to retry {Item}", item.Message);

            if (_cancellationToken.IsCancellationRequested) return;

            if (item.Attempts < MaximumAttempts)
            {
                _block.Post(item);
            }
            else
            {
                _logger.LogInformation("Discarding message {Message} after {Attempts} attempts", item.Message,
                    item.Attempts);

                throw;
            }
        }
    }

    public Task DrainAsync()
    {
        _block.Complete();
        return _block.Completion;
    }

    public class Item
    {
        public Item(T item)
        {
            Message = item;
            Attempts = 0;
        }

        public int Attempts { get; set; }
        public T Message { get; }
    }
}