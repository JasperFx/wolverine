using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
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

    public InlineAzureServiceBusSender(AzureServiceBusEndpoint endpoint, IOutgoingMapper<ServiceBusMessage> mapper, ServiceBusSender sender, ILogger logger, CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination => _endpoint.Uri;
    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);
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
        await _sender.SendMessageAsync(message, _cancellationToken);
    }
}