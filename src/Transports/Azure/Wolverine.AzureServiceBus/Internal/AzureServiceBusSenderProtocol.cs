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

        var messages = new List<ServiceBusMessage>(batch.Messages.Count);

        foreach (var envelope in batch.Messages)
        {
            try
            {
                var message = new ServiceBusMessage();
                _mapper.MapEnvelopeToOutgoing(envelope, message);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Error trying to translate envelope {Envelope} to a ServiceBusMessage object. Message will be discarded.",
                    envelope);
            }
        }

        try
        {
            var serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();

            foreach (var message in messages)
            {
                if (!serviceBusMessageBatch.TryAddMessage(message))
                {
                    // Send the currently full batch
                    await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);
                    serviceBusMessageBatch.Dispose();

                    // Create a new batch and add the message to it
                    serviceBusMessageBatch = await _sender.CreateMessageBatchAsync();
                    serviceBusMessageBatch.TryAddMessage(message);
                }
            }

            // Send the final batch
            await _sender.SendMessagesAsync(serviceBusMessageBatch, _runtime.Cancellation);
            serviceBusMessageBatch.Dispose();

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }
}