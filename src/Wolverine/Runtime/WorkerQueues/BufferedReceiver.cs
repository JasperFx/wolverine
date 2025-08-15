using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.WorkerQueues;

internal class BufferedReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue
{
    private readonly RetryBlock<Envelope> _completeBlock;

    private readonly ISender? _deadLetterSender;
    private readonly RetryBlock<Envelope> _deferBlock;
    private readonly Endpoint _endpoint;
    private readonly ILogger _logger;
    private readonly RetryBlock<Envelope>? _moveToErrors;
    private readonly Block<Envelope> _receivingBlock;
    private readonly InMemoryScheduledJobProcessor _scheduler;
    private readonly DurabilitySettings _settings;
    private bool _latched;

    public BufferedReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<BufferedReceiver>();
        _settings = runtime.DurabilitySettings;
        Pipeline = pipeline;

        _scheduler = new InMemoryScheduledJobProcessor(this);

        endpoint.ExecutionOptions.CancellationToken = _settings.Cancellation;

        _deferBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.DeferAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);
        _completeBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.CompleteAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);

        _receivingBlock = new Block<Envelope>(endpoint.ExecutionOptions.MaxDegreeOfParallelism, async (envelope, _) =>
        {
            if (_latched && envelope.Listener != null)
            {
                await _deferBlock.PostAsync(envelope);
                return;
            }

            try
            {
                if (envelope.ContentType.IsEmpty())
                {
                    envelope.ContentType = EnvelopeConstants.JsonContentType;
                }

                await Pipeline.InvokeAsync(envelope, this);
            }
            catch (Exception? e)
            {
                // This *should* never happen, but of course it will
                _logger.LogError(e, "Unexpected error in Pipeline invocation");
            }
        });

        if (endpoint.TryBuildDeadLetterSender(runtime, out var dlq))
        {
            _deadLetterSender = dlq;

            _moveToErrors = new RetryBlock<Envelope>(
                async (envelope, _) => { await _deadLetterSender!.SendAsync(envelope); }, _logger,
                _settings.Cancellation);
        }
    }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    async ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        if (envelope.Listener == null)
        {
            await EnqueueAsync(envelope);
            return;
        }

        try
        {
            var nativelyRequeued = await envelope.Listener.TryRequeueAsync(envelope);
            if (!nativelyRequeued)
            {
                await EnqueueAsync(envelope);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to use native dead letter queue for {Uri}", Uri);
            await EnqueueAsync(envelope);
        }
    }

    public IHandlerPipeline? Pipeline { get; }

    public Uri Uri { get; }

    public int QueueCount => (int)_receivingBlock.Count;

    public async ValueTask DrainAsync()
    {
        _latched = true;
        _receivingBlock.Complete();

        await _completeBlock.DrainAsync();
        await _deferBlock.DrainAsync();

        if (_moveToErrors != null)
        {
            await _moveToErrors.DrainAsync();
        }

        // It hangs, nothing to be done about this I think
        //await _receivingBlock.Completion;
    }

    public void Enqueue(Envelope envelope)
    {
        if (envelope.IsPing())
        {
            return;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;
        _receivingBlock.Post(envelope);
        activity?.Stop();
    }

    public async ValueTask EnqueueAsync(Envelope envelope)
    {
        if (envelope.IsPing())
        {
            return;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;
        await _receivingBlock.PostAsync(envelope);
        activity?.Stop();
    }

    async ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope[] messages)
    {
        var now = DateTimeOffset.Now;

        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        foreach (var envelope in messages)
        {
            envelope.MarkReceived(listener, now, _settings);
            if (!envelope.IsExpired())
            {
                await EnqueueAsync(envelope);
            }

            await _completeBlock.PostAsync(envelope);
        }

        _logger.IncomingBatchReceived(Uri, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        var now = DateTimeOffset.Now;
        envelope.MarkReceived(listener, now, _settings);

        if (envelope.IsExpired())
        {
            await _completeBlock.PostAsync(envelope);
            return;
        }

        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            _scheduler.Enqueue(envelope.ScheduledTime!.Value, envelope);
        }
        else
        {
            await EnqueueAsync(envelope);
        }

        await _completeBlock.PostAsync(envelope);

        _logger.IncomingReceived(envelope, Uri);
    }

    public void Dispose()
    {
        _receivingBlock.Complete();
        _completeBlock.Dispose();
        _deferBlock.Dispose();

        if (_deadLetterSender is IDisposable d)
        {
            d.SafeDispose();
        }

        _moveToErrors?.Dispose();
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        return _moveToErrors!.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => _deadLetterSender != null;

    Task ISupportNativeScheduling.MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.ScheduledTime = time;
        ScheduleExecution(envelope);

        return Task.CompletedTask;
    }

    public void ScheduleExecution(Envelope envelope)
    {
        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope),
                $"There is no {nameof(Envelope.ScheduledTime)} value");
        }

        _scheduler.Enqueue(envelope.ScheduledTime.Value, envelope);
    }
}