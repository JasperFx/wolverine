using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Baseline;
using Wolverine.Logging;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

internal class BufferedReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling
{
    private readonly ILogger _logger;
    private readonly ActionBlock<Envelope> _receivingBlock;
    private readonly InMemoryScheduledJobProcessor _scheduler;
    private readonly AdvancedSettings _settings;
    private bool _latched = false;

    public BufferedReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _logger = runtime.Logger;
        _settings = runtime.Advanced;
        Pipeline = pipeline;

        _scheduler = new InMemoryScheduledJobProcessor(this);

        endpoint.ExecutionOptions.CancellationToken = _settings.Cancellation;

        _receivingBlock = new ActionBlock<Envelope>(async envelope =>
        {
            if (_latched && envelope.Listener != null)
            {
                await envelope.Listener.DeferAsync(envelope);
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
        }, endpoint.ExecutionOptions);
    }

    public int QueueCount => _receivingBlock.InputCount;

    public async ValueTask DrainAsync()
    {
        _latched = true;
        _receivingBlock.Complete();
        await _receivingBlock.Completion;
    }

    public IHandlerPipeline Pipeline { get; }

    public Uri? Address { get; set; }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    async ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        if (envelope.Listener == null)
        {
            Enqueue(envelope);
            return;
        }

        var nativelyRequeued = await envelope.Listener.TryRequeueAsync(envelope);
        if (!nativelyRequeued)
        {
            Enqueue(envelope);
        }
    }

    Task ISupportNativeScheduling.MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.ScheduledTime = time;
        ScheduleExecution(envelope);

        return Task.CompletedTask;
    }

    public void Enqueue(Envelope envelope)
    {
        if (envelope.IsPing())
        {
            return;
        }

        var activity = WolverineTracing.StartReceiving(envelope);
        _receivingBlock.Post(envelope);
        activity?.Stop();
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
            Enqueue(envelope);
            await listener.CompleteAsync(envelope);
        }

        _logger.IncomingBatchReceived(messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        var now = DateTimeOffset.Now;
        envelope.MarkReceived(listener, now, _settings);

        if (envelope.IsExpired())
        {
            return;
        }

        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            _scheduler.Enqueue(envelope.ScheduledTime!.Value, envelope);
        }
        else
        {
            Enqueue(envelope);
        }

        await listener.CompleteAsync(envelope);

        _logger.IncomingReceived(envelope, Address);
    }

    public void Dispose()
    {
        _receivingBlock.Complete();
    }
}
