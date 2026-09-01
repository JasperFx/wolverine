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
    IReportConnectionState, ISupportLeaseRenewal
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
        ISender requeue, int maximumAckAttempts)
    {
        _endpoint = endpoint;
        _logger = logger;
        _wolverineReceiver = wolverineReceiver;
        _receiver = receiver;
        _mapper = mapper;
        _requeue = requeue;

        _task = Task.Run(listenForMessages, _cancellation.Token);

        // GH-4012 item 3: ack budget + terminal-failure classification, shared with the inline listener
        _complete = AzureServiceBusSettlement.CompleteBlock(
            (e, _) => AzureServiceBusSettlement.CompleteAsync(e, maximumAckAttempts, _logger, _cancellation.Token),
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

    /// <summary>
    /// GH-4048. The entity's own lock duration is the clock on an unsettled Azure Service Bus delivery, and
    /// <c>LeaseRenewalTracker</c> ticks at half of it.
    /// </summary>
    public TimeSpan LeaseDuration => _endpoint.LockDuration;

    public TimeSpan MaximumLeaseExtension => _endpoint.MaximumLockRenewalDuration;

    /// <summary>
    /// GH-4048/GH-4051. Re-arm the broker's lock on these queued-but-unsettled deliveries.
    ///
    /// <para>
    /// This is deliberately NOT <c>ServiceBusProcessorOptions.MaxAutoLockRenewalDuration</c>, which is the obvious
    /// thing to reach for and is useless here: the SDK's auto-renewal runs only while the processor's callback is on
    /// the stack. Under <see cref="EndpointMode.NativeAck" /> the delivery is handed to an execution lane and the
    /// receive loop moves on immediately, so an endpoint configured that way would LOOK protected and be renewing
    /// nothing for the whole time the envelope is queued. It also does not apply to this class at all, which uses a
    /// <c>ServiceBusReceiver</c> rather than a processor.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Azure Service Bus has no batch lock-renewal call, so this is one round trip per envelope, issued serially --
    /// the tick only ever covers what is actually in the lanes, and the lane count is bounded by
    /// <c>MaxDegreeOfParallelism</c> (or the partition slot count).
    ///
    /// <para>
    /// Only a refusal that means the lock is GONE is reported as a lost lease. Everything else -- a throttle, a
    /// timeout, a dropped AMQP link -- leaves the envelope tracked so the next tick retries it, which is what the
    /// contract asks for, and is why this catches per envelope rather than letting one bad call abandon the rest of
    /// the batch.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes,
        CancellationToken token)
    {
        List<Envelope>? lost = null;

        foreach (var envelope in envelopes)
        {
            if (envelope is not AzureServiceBusEnvelope e) continue;

            // Nothing to renew on a delivery that has already been settled by a terminal that has not yet
            // untracked it
            if (e.IsCompleted) continue;

            token.ThrowIfCancellationRequested();

            try
            {
                await _receiver.RenewMessageLockAsync(e.AzureMessage, token);
            }
            catch (Exception ex) when (isLockLost(ex))
            {
                // The broker owns this delivery again and is going to redeliver it. Core decides what happens
                // next; all this can do is report it accurately.
                (lost ??= []).Add(envelope);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient by elimination. Explicitly NOT a lost lease -- reporting one here would make core drop
                // a delivery whose lock is very probably still perfectly good.
                _logger.LogDebug(ex,
                    "Transient failure renewing the Azure Service Bus lock on message {Id} at {Uri}; it stays tracked and the next tick will retry it",
                    e.AzureMessage.MessageId, _endpoint.Uri);
            }
        }

        return lost ?? (IReadOnlyList<Envelope>)[];
    }

    /// <summary>
    /// Does this failure mean the lock is gone for good? <c>MessageLockLost</c> is the direct answer and
    /// <c>MessageNotFound</c> means the message is no longer there to hold a lock at all. The message-text check
    /// is the same defensive one <see cref="AzureServiceBusEnvelope.CompleteAsync" /> already carries, for
    /// implementations that report an expired lock without setting the typed reason.
    /// </summary>
    private static bool isLockLost(Exception ex)
    {
        return ex is ServiceBusException e && (e.Reason is ServiceBusFailureReason.MessageLockLost
                                                   or ServiceBusFailureReason.MessageNotFound
                                               || e.Message.ContainsIgnoreCase("The lock supplied is invalid"));
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

                // GH-4215. A missing entity is not a transient blip: the emulator restarting empty, an operator
                // or IaC teardown, a broker wipe. Retrying the receive cannot bring it back, so the old
                // one-second cadence bought nothing and cost ~23 error lines a second across a fleet. The
                // entity was declared by AutoProvision at startup, so the application knows how to declare it --
                // nothing simply re-ran that afterwards.
                var entityMissing = e is ServiceBusException
                {
                    Reason: ServiceBusFailureReason.MessagingEntityNotFound
                };

                var pauseTime = entityMissing
                    ? 5.Seconds()
                    : failedCount > 5
                        ? 1.Seconds()
                        : (failedCount * 100).Milliseconds();

                if (entityMissing)
                {
                    // First of a streak and then every 60th, rather than every iteration -- the log storm was
                    // its own outage on top of the one being reported.
                    if (failedCount == 1 || failedCount % 60 == 0)
                    {
                        _logger.LogWarning(e,
                            "The Azure Service Bus entity for {Uri} does not exist (consecutive failures: {Count}). Retrying the receive cannot succeed until it is re-declared.",
                            _endpoint.Uri, failedCount);
                    }

                    await tryRedeclareAsync();
                }
                else if (_endpoint.Role == EndpointRole.Application)
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

    /// <summary>
    /// GH-4215. Re-declare the entity so a wiped broker heals the way a startup does. Gated on AutoProvision:
    /// re-creating an entity the application never created is not Wolverine's call to make, and without it the
    /// loop still backs off and reports rather than retrying once a second forever.
    /// </summary>
    private async Task tryRedeclareAsync()
    {
        if (!_endpoint.Parent.AutoProvision) return;

        try
        {
            await _endpoint.SetupAsync(_logger);
        }
        catch (Exception redeclare)
        {
            // Best-effort by nature: the broker being unreachable is exactly when this fails, and it must
            // never take the receive loop down with it.
            _logger.LogWarning(redeclare, "Could not re-declare the Azure Service Bus entity for {Uri}",
                _endpoint.Uri);
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