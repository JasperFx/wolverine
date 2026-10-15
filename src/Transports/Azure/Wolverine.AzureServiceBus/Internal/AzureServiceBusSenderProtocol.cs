using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusSenderProtocol : ISenderProtocolWithNativeScheduling
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IOutgoingMapper<ServiceBusMessage> _mapper;
    private readonly IWolverineRuntime _runtime;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<AzureServiceBusSenderProtocol> _logger;
    private readonly ScheduledMessageCancellationRegistry? _cancellations;

    public AzureServiceBusSenderProtocol(IWolverineRuntime runtime, AzureServiceBusEndpoint endpoint,
        IOutgoingMapper<ServiceBusMessage> mapper, ServiceBusSender sender)
    {
        _runtime = runtime;
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
        _logger = runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>();
        _cancellations = (runtime as IWolverineRuntimeInternal)?.ScheduledMessageCancellations;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _endpoint.InitializeAsync(_logger);

        var messages = new List<(Envelope Envelope, ServiceBusMessage Message)>(batch.Messages.Count);

        foreach (var envelope in batch.Messages)
        {
            try
            {
                var message = new ServiceBusMessage();
                _mapper.MapEnvelopeToOutgoing(envelope, message);
                messages.Add((envelope, message));
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Error trying to translate envelope {Envelope} to a ServiceBusMessage object. Message will be discarded.",
                    envelope);
            }
        }

        if (_endpoint.IsPartitioned)
            await sendPartitionedBatches(callback, messages, batch);
        else
            await sendBatches(callback, messages, batch);
    }

    private async Task sendBatches(ISenderCallback callback, List<(Envelope Envelope, ServiceBusMessage Message)> messages, OutgoingMessageBatch batch)
    {
        var sentEnvelopes = new List<Envelope>();
        var pendingEnvelopes = new List<Envelope>();
        ServiceBusMessageBatch? currentBatch = null;

        try
        {
            foreach (var (envelope, message) in messages)
            {
                var cancellation = tryFindCancellation(envelope);
                if (cancellation?.IsBound == true)
                {
                    sentEnvelopes.Add(envelope);
                    continue;
                }

                if (cancellation != null && envelope.ScheduledTime is { } scheduledTime &&
                    scheduledTime <= DateTimeOffset.UtcNow)
                {
                    cancellation.MarkNotScheduled();
                    message.ScheduledEnqueueTime = default;
                }
                else if (cancellation != null && envelope.ScheduledTime is { } futureTime)
                {
                    if (currentBatch != null)
                    {
                        await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
                        currentBatch.Dispose();
                        currentBatch = null;
                    }

                    await scheduleCancellableAsync(cancellation, message, futureTime);
                    sentEnvelopes.Add(envelope);
                    continue;
                }

                currentBatch ??= await _sender.CreateMessageBatchAsync();
                if (!currentBatch.TryAddMessage(message))
                {
                    // If the batch is empty and the message still doesn't fit, it's too large for any batch
                    if (currentBatch.Count == 0)
                    {
                        throw new MessageTooLargeException(envelope, currentBatch.MaxSizeInBytes,
                                AzureServiceBusTransport.MessageTooLargeRemedy(_endpoint.Uri));
                    }

                    _logger.LogInformation("Wolverine had to break up outgoing message batches at {Uri}, you may want to reduce the MaximumMessagesToReceive configuration. No messages were lost, this is strictly informative", _endpoint.Uri);

                    // Send the currently full batch
                    await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
                    currentBatch.Dispose();

                    // Create a new batch and add the message to it
                    currentBatch = await _sender.CreateMessageBatchAsync();
                    if (!currentBatch.TryAddMessage(message))
                    {
                        throw new MessageTooLargeException(envelope, currentBatch.MaxSizeInBytes,
                            AzureServiceBusTransport.MessageTooLargeRemedy(_endpoint.Uri));
                    }
                }

                pendingEnvelopes.Add(envelope);
            }

            // Send the final batch
            if (currentBatch != null)
            {
                await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
            }

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            if (sentEnvelopes.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, sentEnvelopes));
            }

            var failedEnvelopes = batch.Messages.Where(env => !sentEnvelopes.Contains(env)).ToList();
            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failedEnvelopes), e);
        }
        finally
        {
            currentBatch?.Dispose();
        }
    }

    private async Task sendPartitionedBatches(ISenderCallback callback, List<(Envelope Envelope, ServiceBusMessage Message)> messages, OutgoingMessageBatch batch)
    {
        var sentEnvelopes = new List<Envelope>();
        Exception? lastException = null;

        var groupedMessages = messages
            .GroupBy(x => x.Message.SessionId)
            .ToList();

        foreach (var group in groupedMessages)
        {
            var pendingEnvelopes = new List<Envelope>();
            ServiceBusMessageBatch? currentBatch = null;

            try
            {
                _logger.LogDebug("Processing batch with session id '{SessionId}'", group.Key);

                foreach (var (envelope, message) in group)
                {
                    var cancellation = tryFindCancellation(envelope);
                    if (cancellation?.IsBound == true)
                    {
                        sentEnvelopes.Add(envelope);
                        continue;
                    }

                    if (cancellation != null && envelope.ScheduledTime is { } scheduledTime &&
                        scheduledTime <= DateTimeOffset.UtcNow)
                    {
                        cancellation.MarkNotScheduled();
                        message.ScheduledEnqueueTime = default;
                    }
                    else if (cancellation != null && envelope.ScheduledTime is { } futureTime)
                    {
                        if (currentBatch != null)
                        {
                            await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
                            currentBatch.Dispose();
                            currentBatch = null;
                        }

                        await scheduleCancellableAsync(cancellation, message, futureTime);
                        sentEnvelopes.Add(envelope);
                        continue;
                    }

                    currentBatch ??= await _sender.CreateMessageBatchAsync();
                    if (!currentBatch.TryAddMessage(message))
                    {
                        // If the batch is empty and the message still doesn't fit, it's too large for any batch
                        if (currentBatch.Count == 0)
                        {
                            throw new MessageTooLargeException(envelope, currentBatch.MaxSizeInBytes,
                                AzureServiceBusTransport.MessageTooLargeRemedy(_endpoint.Uri));
                        }

                        _logger.LogInformation("Wolverine had to break up outgoing message batches at {Uri}, you may want to reduce the MaximumMessagesToReceive configuration. No messages were lost, this is strictly informative", _endpoint.Uri);

                        // Send the currently full batch
                        await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
                        currentBatch.Dispose();

                        // Create a new batch and add the message to it
                        currentBatch = await _sender.CreateMessageBatchAsync();
                        if (!currentBatch.TryAddMessage(message))
                        {
                            throw new MessageTooLargeException(envelope, currentBatch.MaxSizeInBytes,
                                AzureServiceBusTransport.MessageTooLargeRemedy(_endpoint.Uri));
                        }
                    }

                    pendingEnvelopes.Add(envelope);
                }

                // Send the final batch for this session group
                if (currentBatch != null)
                {
                    await flushBatchAsync(currentBatch, pendingEnvelopes, sentEnvelopes);
                }
            }
            catch (Exception e)
            {
                lastException = e;
                break;
            }
            finally
            {
                currentBatch?.Dispose();
            }
        }

        if (lastException != null)
        {
            if (sentEnvelopes.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, sentEnvelopes));
            }

            var failedEnvelopes = batch.Messages.Where(env => !sentEnvelopes.Contains(env)).ToList();
            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failedEnvelopes), lastException);
        }
        else
        {
            await callback.MarkSuccessfulAsync(batch);
        }
    }

    private ScheduledMessageCancellationRegistration? tryFindCancellation(Envelope envelope)
    {
        return envelope.ScheduledCancellation ?? _cancellations?.Find(envelope.Id);
    }

    private async ValueTask scheduleCancellableAsync(ScheduledMessageCancellationRegistration registration,
        ServiceBusMessage message, DateTimeOffset scheduledTime)
    {
        if (registration.IsBound || !registration.CanBeBound)
        {
            return;
        }

        var sequenceNumber = await _sender.ScheduleMessageAsync(message, scheduledTime, _runtime.Cancellation)
            .ConfigureAwait(false);
        registration.TryBind(token => AzureServiceBusScheduledCancellation.CancelAsync(_sender, sequenceNumber, token));
    }

    private async Task flushBatchAsync(ServiceBusMessageBatch messageBatch, List<Envelope> pendingEnvelopes,
        List<Envelope> sentEnvelopes)
    {
        if (pendingEnvelopes.Count == 0)
        {
            return;
        }

        await _sender.SendMessagesAsync(messageBatch, _runtime.Cancellation).ConfigureAwait(false);
        sentEnvelopes.AddRange(pendingEnvelopes);
        pendingEnvelopes.Clear();
    }
}
