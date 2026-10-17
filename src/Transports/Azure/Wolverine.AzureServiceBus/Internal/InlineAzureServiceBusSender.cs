using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class InlineAzureServiceBusSender : ISender
{
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IOutgoingMapper<ServiceBusMessage> _mapper;
    private readonly ServiceBusSender _sender;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly ScheduledMessageCancellationRegistry? _cancellations;

    public InlineAzureServiceBusSender(AzureServiceBusEndpoint endpoint, IOutgoingMapper<ServiceBusMessage> mapper, ServiceBusSender sender, ILogger logger, CancellationToken cancellationToken)
        : this(endpoint, mapper, sender, logger, cancellationToken, null)
    {
    }

    internal InlineAzureServiceBusSender(AzureServiceBusEndpoint endpoint, IOutgoingMapper<ServiceBusMessage> mapper,
        ServiceBusSender sender, ILogger logger, CancellationToken cancellationToken,
        ScheduledMessageCancellationRegistry? cancellations)
    {
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _cancellations = cancellations;
    }

    public bool SupportsNativeScheduledSend => true;
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

        // The listener reuses an incoming envelope when it sends a retry or deferral. Its ID can
        // still match the original schedule's live cancellation registration, but that registration
        // must only deduplicate outgoing outbox work (including recovery), not listener requeues.
        var registration = envelope.Status == EnvelopeStatus.Outgoing
            ? envelope.ScheduledCancellation ?? _cancellations?.Find(envelope.Id)
            : null;
        if (registration?.IsBound == true)
        {
            return;
        }

        if (registration != null && envelope.ScheduledTime is { } scheduledTime)
        {
            if (scheduledTime <= DateTimeOffset.UtcNow)
            {
                registration.MarkNotScheduled();
                message.ScheduledEnqueueTime = default;
            }
            else
            {
                if (registration.CanBeBound)
                {
                    var sequenceNumber = await _sender.ScheduleMessageAsync(message, scheduledTime, _cancellationToken)
                        .ConfigureAwait(false);
                    registration.TryBind(token => AzureServiceBusScheduledCancellation.CancelAsync(_sender, sequenceNumber, token));
                }

                return;
            }
        }

        await _sender.SendMessageAsync(message, _cancellationToken);
    }
}
