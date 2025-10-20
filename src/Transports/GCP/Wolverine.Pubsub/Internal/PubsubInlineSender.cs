using Google.Cloud.PubSub.V1;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

internal class PubsubInlineSender : ISender, IAsyncDisposable
{
    private readonly PubsubTopic _topic;
    private PublisherClient? _client;
    private readonly IPubsubEnvelopeMapper _mapper;

    public PubsubInlineSender(PubsubTopic topic, IWolverineRuntime runtime)
    {
        _topic = topic;
        Destination = topic.Uri;
        _mapper = _topic.BuildMapper(runtime);
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }
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
        if (_client == null)
        {
            var builder = new PublisherClientBuilder()
            {
                TopicName = _topic.TopicName,
                EmulatorDetection = _topic.Parent.EmulatorDetection
            };

            _client = await builder.BuildAsync();
        }

        var message = new PubsubMessage();
        _mapper.MapEnvelopeToOutgoing(envelope, message);
        await _client.PublishAsync(message);
    }

    public ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            return _client.DisposeAsync();
        }

        return new ValueTask();
    }
}