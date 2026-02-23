using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class InlineAzureServiceBusSender : ISenderWithScheduledCancellation
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IOutgoingMapper<ServiceBusMessage> _mapper;
    private readonly ServiceBusSender _sender;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;

    public InlineAzureServiceBusSender(AzureServiceBusEndpoint endpoint, IOutgoingMapper<ServiceBusMessage> mapper, ServiceBusSender sender, ILogger logger, CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    public bool SupportsNativeScheduledSend => true;
    public bool SupportsNativeScheduledCancellation => true;
    public Uri Destination => _endpoint.Uri;
    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);

        // For GH-1230, and according to Azure Service Bus docs, it does not harm
        // to send a session identifier to a non-FIFO queue, so just do this by default
        envelope.GroupId = Guid.NewGuid().ToString();

        try
        {
            await SendAsync(envelope);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        await _endpoint.InitializeAsync(_logger);
        var message = new ServiceBusMessage();
        _mapper.MapEnvelopeToOutgoing(envelope, message);

        if (envelope.ScheduledTime.HasValue)
        {
            // Note: The mapper already sets message.ScheduledEnqueueTime above.
            // ScheduleMessageAsync's scheduledEnqueueTime parameter takes precedence
            // over the message property per the ASB SDK docs. We pass the same value
            // for consistency.
            var sequenceNumber = await _sender.ScheduleMessageAsync(
                message, message.ScheduledEnqueueTime, _cancellationToken);
            envelope.SchedulingToken = sequenceNumber;
        }
        else
        {
            await _sender.SendMessageAsync(message, _cancellationToken);
        }
    }

    public async Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        if (schedulingToken is not long sequenceNumber)
        {
            throw new ArgumentException(
                $"Expected scheduling token of type long for Azure Service Bus, got {schedulingToken?.GetType().Name ?? "null"}",
                nameof(schedulingToken));
        }

        await _endpoint.InitializeAsync(_logger);
        await _sender.CancelScheduledMessageAsync(sequenceNumber, cancellation);
    }
}