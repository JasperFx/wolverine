using Azure.Messaging.ServiceBus;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class BatchedAzureServiceBusListener : IListener, ISupportDeadLetterQueue, ISupportNativeScheduling,
    IReportConnectionState
{
    private readonly CancellationTokenSource _cancellation = new();

    // GH-3237: derived only from real receive-loop failures (degrade-only). A successful receive clears back
    // to Unknown — never Connected, because the SDK cannot prove the AMQP link is up without traffic.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Unknown;
    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;
    private readonly RetryBlock<Envelope> _defer;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IIncomingMapper<ServiceBusReceivedMessage> _mapper;
    private readonly ServiceBusReceiver _receiver;
    private readonly ISender _requeue;
    private readonly Task _task;
    private readonly IReceiver _wolverineReceiver;

    public BatchedAzureServiceBusListener(AzureServiceBusEndpoint endpoint, ILogger logger,
        IReceiver wolverineReceiver, ServiceBusReceiver receiver, IIncomingMapper<ServiceBusReceivedMessage> mapper,
        ISender requeue)
    {
        _endpoint = endpoint;
        _logger = logger;
        _wolverineReceiver = wolverineReceiver;
        _receiver = receiver;
        _mapper = mapper;
        _requeue = requeue;

        _task = Task.Run(listenForMessages, _cancellation.Token);

        _complete = new RetryBlock<AzureServiceBusEnvelope>((e, _) => { return e.CompleteAsync(_cancellation.Token); },
            _logger, _cancellation.Token);

        _defer = new RetryBlock<Envelope>(async (envelope, _) =>
        {
            // GH-3494 (AO8): settle the original before re-sending the copy, exactly like the
            // inline listener already does. Leaving it unsettled meant the message stayed locked
            // until the lock expired and Azure Service Bus redelivered it -- so every deferral
            // produced a duplicate on top of the copy this block sends.
            if (envelope is AzureServiceBusEnvelope { IsCompleted: false } e)
            {
                await e.CompleteAsync(_cancellation.Token);
            }

            await _requeue.SendAsync(envelope);
        }, logger, _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>(
                (e, c) => e.DeadLetterAsync(_cancellation.Token, e.Exception?.GetType().NameInCode(),
                    e.Exception?.Message), logger,
                _cancellation.Token);
    }

    public IHandlerPipeline? Pipeline => _wolverineReceiver.Pipeline;

    public TransportConnectionState ConnectionState => _connectionState;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _complete.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _defer.PostAsync(envelope);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope)
        {
            await _defer.PostAsync(envelope);
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.Dispose();
        _task.SafeDispose();
        _complete.SafeDispose();
        _defer.SafeDispose();
        _deadLetter.SafeDispose();
    }

    public Uri Address => _endpoint.Uri;

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        await _receiver.CloseAsync();
    }

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }

    public bool NativeDeadLetterQueueEnabled => true;

    /// <summary>
    /// GH-4049. Claimed for <see cref="EndpointMode.NativeAck" /> only, and the narrowness is deliberate.
    /// <see cref="Wolverine.Runtime.MessageContext.ReScheduleAsync" /> prefers the listener over the pipeline channel,
    /// so answering true unconditionally would take the reschedule away from receivers that already own it:
    /// <c>DurableReceiver</c>, where the envelope has an inbox row of its own and re-publishing a copy under the same
    /// id would be discarded as a duplicate on redelivery (the reason RedisStreamListener opts out of Durable too),
    /// and <c>BufferedReceiver</c>, which supplies an in-memory rescheduler. NativeAck is the mode with no
    /// rescheduler at all: without this, a scheduled retry falls through to <c>Storage.Inbox</c> and breaks the
    /// storage-free guarantee the mode exists to provide (GH-3708).
    /// </summary>
    public bool NativeSchedulingEnabled => _endpoint.Mode == EndpointMode.NativeAck;

    /// <summary>
    /// Reschedule by re-publishing the envelope as an Azure Service Bus scheduled message -- the outgoing mapper
    /// turns <see cref="Envelope.ScheduledTime" /> into <c>ServiceBusMessage.ScheduledEnqueueTime</c>, so the broker
    /// holds it, not Wolverine. Same mechanism the inline listener uses (InlineAzureServiceBusListener), routed
    /// through this listener's existing <c>_defer</c> block so the original delivery is settled first: leaving it
    /// locked means the broker redelivers it at lock expiry and every reschedule costs a duplicate. That is exactly
    /// the GH-3494 (AO8) defect the block was fixed for.
    /// </summary>
    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.ScheduledTime = time;
        await _defer.PostAsync(envelope);
    }

    private async Task listenForMessages()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await _receiver
                    .ReceiveMessagesAsync(_endpoint.MaximumMessagesToReceive,
                        _endpoint.MaximumWaitTime, _cancellation.Token);

                failedCount = 0;

                if (_connectionState != TransportConnectionState.Unknown)
                {
                    // The receive succeeded, so any previously derived trouble state is stale
                    _connectionState = TransportConnectionState.Unknown;
                }

                if (messages.Any())
                {
                    var envelopes = new List<Envelope>(messages.Count);
                    foreach (var message in messages)
                    {
                        try
                        {
                            var envelope = new AzureServiceBusEnvelope(message, _receiver);
                            _mapper.MapIncomingToEnvelope(envelope, message);

                            envelopes.Add(envelope);
                        }
                        catch (Exception e)
                        {
                            await tryMoveToDeadLetterQueue(message);
                            _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId,
                                _endpoint.Uri);
                        }
                    }

                    if (envelopes.Any())
                    {
                        await _wolverineReceiver.ReceivedAsync(this, envelopes.ToArray());
                    }
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(250.Milliseconds());
                }
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                // The receive attempt genuinely failed and this loop is about to back off and retry, so
                // Reconnecting is the honest floor even for exception types the mapper doesn't recognize
                _connectionState = AzureServiceBusConnectionStateMapper.StateForError(e)
                                   ?? TransportConnectionState.Reconnecting;

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                if (_endpoint.Role == EndpointRole.Application)
                {
                    _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
                        _endpoint.Uri);
                }
                else
                {
                    _logger.LogError(e,
                        "Error while trying to retrieve messages from Azure Service Bus {Uri}. Check if system queues should be enabled for this application because this could be from the application being unable to create the system queues for Azure Service Bus",
                        _endpoint.Uri);
                }

                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task tryMoveToDeadLetterQueue(ServiceBusReceivedMessage message)
    {
        try
        {
            await _receiver.DeadLetterMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure while trying to move message {Id} to the dead letter queue",
                message.MessageId);
        }
    }
}