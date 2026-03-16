using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class CosmosDbControlSender : ISender, IAsyncDisposable
{
    private readonly CosmosDbControlEndpoint _endpoint;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly CosmosDbControlTransport _transport;

    public CosmosDbControlSender(CosmosDbControlEndpoint endpoint, CosmosDbControlTransport transport,
        ILogger logger, CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(sendMessageAsync, logger, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await _transport.Container.ReadContainerAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        envelope.DeliverWithin = 10.Seconds();

        await _retryBlock.PostAsync(envelope);
    }

    private async Task sendMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var partitionKey = ControlMessage.PartitionKeyFor(_endpoint.NodeId);
            var message = new ControlMessage
            {
                Id = envelope.Id.ToString(),
                NodeId = _endpoint.NodeId.ToString(),
                PartitionKey = partitionKey,
                MessageType = envelope.MessageType!,
                Body = EnvelopeSerializer.Serialize(envelope),
                Expires = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                Posted = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await _transport.Container.CreateItemAsync(message,
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }
}
