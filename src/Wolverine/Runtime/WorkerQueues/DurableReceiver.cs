using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Runtime.WorkerQueues;

internal class DurableReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue,
    IAsyncDisposable
{
    private readonly RetryBlock<Envelope> _incrementAttempts;
    protected readonly ILogger _logger;
    private readonly RetryBlock<Envelope> _markAsHandled;
    private readonly RetryBlock<ErrorReport> _moveToErrors;
    private readonly IMessageInbox _inbox;
    private readonly ActionBlock<Envelope> _receiver;
    private readonly RetryBlock<Envelope> _receivingOne;
    private readonly RetryBlock<Envelope> _scheduleExecution;
    private readonly DurabilitySettings _settings;

    // These members are for draining
    private bool _latched;

    public DurableReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _settings = runtime.DurabilitySettings;
        _inbox = runtime.Storage.Inbox;
        _logger = runtime.LoggerFactory.CreateLogger<DurableReceiver>();

        Uri = endpoint.Uri;

        endpoint.ExecutionOptions.CancellationToken = _settings.Cancellation;

        _receiver = new ActionBlock<Envelope>(async envelope =>
        {
            if (_latched)
            {
                return;
            }

            try
            {
                envelope.ContentType ??= EnvelopeConstants.JsonContentType;

                await pipeline.InvokeAsync(envelope, this);
            }
            catch (Exception? e)
            {
                _receiver?.Post(envelope);

                // This *should* never happen, but of course it will
                _logger.LogError(e, "Unexpected pipeline invocation error");
            }
        }, endpoint.ExecutionOptions);

        _markAsHandled = new RetryBlock<Envelope>((e, _) => _inbox.MarkIncomingEnvelopeAsHandledAsync(e), _logger,
            _settings.Cancellation);
        _incrementAttempts = new RetryBlock<Envelope>((e, _) => _inbox.IncrementIncomingEnvelopeAttemptsAsync(e),
            _logger, _settings.Cancellation);
        _scheduleExecution = new RetryBlock<Envelope>((e, _) => _inbox.ScheduleExecutionAsync(e),
            _logger, _settings.Cancellation);
        _moveToErrors = new RetryBlock<ErrorReport>(
            (report, _) => _inbox.MoveToDeadLetterStorageAsync(report.Envelope, report.Exception), _logger,
            _settings.Cancellation);

        _receivingOne = new RetryBlock<Envelope>((e, _) => receiveOneAsync(e), _logger, _settings.Cancellation);
    }

    public Uri Uri { get; }

    public async ValueTask DisposeAsync()
    {
        _receiver.Complete();
        await _receiver.Completion;

        _incrementAttempts.Dispose();
        _scheduleExecution.Dispose();
        _markAsHandled.Dispose();
        _moveToErrors.Dispose();
        _receivingOne.Dispose();
    }


    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask(_markAsHandled.PostAsync(envelope));
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        envelope.Attempts++;

        Enqueue(envelope);

        return new ValueTask(_incrementAttempts.PostAsync(envelope));
    }

    public int QueueCount => _receiver.InputCount;

    public void Enqueue(Envelope envelope)
    {
        envelope.ReplyUri = envelope.ReplyUri ?? Uri;
        _receiver.Post(envelope);
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        var now = DateTimeOffset.UtcNow;

        return ProcessReceivedMessagesAsync(now, listener, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        if (listener == null)
        {
            throw new ArgumentNullException(nameof(listener));
        }

        if (envelope == null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        using var activity = WolverineTracing.StartReceiving(envelope);
        try
        {
            var now = DateTimeOffset.UtcNow;
            envelope.MarkReceived(listener, now, _settings);

            await _receivingOne.PostAsync(envelope);
        }
        finally
        {
            activity?.Stop();
        }
    }

    public async ValueTask DrainAsync()
    {
        _latched = true;
        _receiver.Complete();
        await _receiver.Completion;

        await _incrementAttempts.DrainAsync();
        await _scheduleExecution.DrainAsync();
        await _markAsHandled.DrainAsync();
        await _moveToErrors.DrainAsync();
        await _receivingOne.DrainAsync();

        await executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.NodeLockId, Uri));
    }


    public void Dispose()
    {
        // Might need to drain the block
        _receiver.Complete();
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        var errorReport = new ErrorReport(envelope, exception);

        return _moveToErrors.PostAsync(errorReport);
    }

    public Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.ScheduledTime = time;
        envelope.Status = EnvelopeStatus.Scheduled;

        return _scheduleExecution.PostAsync(envelope);
    }

    private async Task receiveOneAsync(Envelope envelope)
    {
        try
        {
            await _inbox.StoreIncomingAsync(envelope);
        }
        catch (DuplicateIncomingEnvelopeException e)
        {
            _logger.LogError(e, "Duplicate incoming envelope detected");

            await envelope.Listener!.CompleteAsync(envelope);
            return;
        }

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            Enqueue(envelope);
        }

        _logger.IncomingReceived(envelope, Uri);
        await envelope.Listener!.CompleteAsync(envelope);
    }

    private async Task executeWithRetriesAsync(Func<Task> action)
    {
        var i = 0;
        while (true)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unexpected failure");
                i++;
                await Task.Delay(i * 100).ConfigureAwait(false);
            }
        }
    }

    // Separated for testing here.
    public async ValueTask ProcessReceivedMessagesAsync(DateTimeOffset now, IListener listener, Envelope[] envelopes)
    {
        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        foreach (var envelope in envelopes) envelope.MarkReceived(listener, now, _settings);

        var batchSucceeded = false;
        try
        {
            await _inbox.StoreIncomingAsync(envelopes);
            batchSucceeded = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to persist incoming envelopes at {Uri}", Uri);

            // Use finer grained retries on one envelope at a time, and this will also deal with
            // duplicate detection
            foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope);
        }

        if (batchSucceeded)
        {
            foreach (var message in envelopes)
            {
                Enqueue(message);
                await listener.CompleteAsync(message);
            }
        }


        _logger.IncomingBatchReceived(Uri, envelopes);
    }

    public Task ClearInFlightIncomingAsync()
    {
        return executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.NodeLockId, Uri));
    }
}