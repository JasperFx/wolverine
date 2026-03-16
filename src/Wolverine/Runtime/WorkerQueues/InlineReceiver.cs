using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

internal class InlineReceiver : IReceiver
{
    private readonly ILogger _logger;
    private readonly Endpoint _endpoint;
    private readonly IHandlerPipeline _pipeline;
    private readonly DurabilitySettings _settings;

    private int _inFlightCount;
    private readonly TaskCompletionSource _drainComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _latched;

    public InlineReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        _pipeline = pipeline;
        _logger = runtime.LoggerFactory.CreateLogger<InlineReceiver>();
        _settings = runtime.DurabilitySettings;
    }

    public IHandlerPipeline Pipeline => _pipeline;

    public int QueueCount => Volatile.Read(ref _inFlightCount);

    public void Dispose()
    {
        // Nothing
    }

    public void Latch()
    {
        _latched = true;
    }

    public ValueTask DrainAsync()
    {
        _latched = true;
        if (Volatile.Read(ref _inFlightCount) == 0)
        {
            _drainComplete.TrySetResult();
        }

        return new ValueTask(_drainComplete.Task.WaitAsync(_settings.DrainTimeout));
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (messages.Length == 0) return;

        Interlocked.Add(ref _inFlightCount, messages.Length);

        foreach (var envelope in messages)
        {
            try
            {
                await ProcessMessageAsync(listener, envelope);
            }
            finally
            {
                DecrementInFlightCount();
            }
        }
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        Interlocked.Increment(ref _inFlightCount);

        try
        {
            await ProcessMessageAsync(listener, envelope);
        }
        finally
        {
            DecrementInFlightCount();
        }
    }

    private async ValueTask ProcessMessageAsync(IListener listener, Envelope envelope)
    {
        if (_latched)
        {
            try
            {
                await listener.DeferAsync(envelope);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deferring envelope {EnvelopeId} after latch", envelope.Id);
            }

            return;
        }

        using var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;

        try
        {
            envelope.MarkReceived(listener, DateTimeOffset.UtcNow, _settings);
            await _pipeline.InvokeAsync(envelope, listener, activity!);
            _logger.IncomingReceived(envelope, listener.Address);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception? e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _logger.LogError(e, "Failure to receive an incoming message for envelope {EnvelopeId}", envelope.Id);

            try
            {
                await listener.DeferAsync(envelope);
            }
            catch (Exception? ex)
            {
                _logger.LogError(ex,
                    "Error when trying to Nack a Rabbit MQ message that failed in the HandlerPipeline ({ConversationId})",
                    envelope.CorrelationId);
            }
        }
        finally
        {
            activity?.Stop();
        }
    }

    private void DecrementInFlightCount()
    {
        if (Interlocked.Decrement(ref _inFlightCount) == 0 && _latched)
        {
            _drainComplete.TrySetResult();
        }
    }
}
