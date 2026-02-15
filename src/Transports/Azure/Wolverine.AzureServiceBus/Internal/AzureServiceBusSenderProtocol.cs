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

    public AzureServiceBusSenderProtocol(IWolverineRuntime runtime, AzureServiceBusEndpoint endpoint,
        IOutgoingMapper<ServiceBusMessage> mapper, ServiceBusSender sender)
    {
        _runtime = runtime;
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
        _logger = runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>();
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

        try
        {
            var serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();

            foreach (var (envelope, message) in messages)
            {
                if (!serviceBusMessageBatch.TryAddMessage(message))
                {
                    // If the batch is empty and the message still doesn't fit, it's too large for any batch
                    if (serviceBusMessageBatch.Count == 0)
                    {
                        serviceBusMessageBatch.Dispose();
                        throw new MessageTooLargeException(envelope, serviceBusMessageBatch.MaxSizeInBytes);
                    }

                    _logger.LogInformation("Wolverine had to break up outgoing message batches at {Uri}, you may want to reduce the MaximumMessagesToReceive configuration. No messages were lost, this is strictly informative", _endpoint.Uri);

                    // Send the currently full batch
                    await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);

                    // All pending envelopes for this sub-batch are now sent
                    sentEnvelopes.AddRange(pendingEnvelopes);
                    pendingEnvelopes.Clear();

                    serviceBusMessageBatch.Dispose();

                    // Create a new batch and add the message to it
                    serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();
                    serviceBusMessageBatch.TryAddMessage(message);
                }

                pendingEnvelopes.Add(envelope);
            }

            // Send the final batch
            await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);
            sentEnvelopes.AddRange(pendingEnvelopes);
            serviceBusMessageBatch.Dispose();

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
            var groupEnvelopes = group.Select(x => x.Envelope).ToList();

            try
            {
                var serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();

                _logger.LogDebug("Processing batch with session id '{SessionId}'", group.Key);

                foreach (var (envelope, message) in group)
                {
                    if (!serviceBusMessageBatch.TryAddMessage(message))
                    {
                        // If the batch is empty and the message still doesn't fit, it's too large for any batch
                        if (serviceBusMessageBatch.Count == 0)
                        {
                            serviceBusMessageBatch.Dispose();
                            throw new MessageTooLargeException(envelope, serviceBusMessageBatch.MaxSizeInBytes);
                        }

                        _logger.LogInformation("Wolverine had to break up outgoing message batches at {Uri}, you may want to reduce the MaximumMessagesToReceive configuration. No messages were lost, this is strictly informative", _endpoint.Uri);

                        // Send the currently full batch
                        await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);
                        serviceBusMessageBatch.Dispose();

                        // Create a new batch and add the message to it
                        serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();
                        serviceBusMessageBatch.TryAddMessage(message);
                    }
                }

                // Send the final batch for this session group
                if (serviceBusMessageBatch.Count > 0)
                {
                    await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);
                }
                serviceBusMessageBatch.Dispose();

                sentEnvelopes.AddRange(groupEnvelopes);
            }
            catch (Exception e)
            {
                lastException = e;
                break;
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
}