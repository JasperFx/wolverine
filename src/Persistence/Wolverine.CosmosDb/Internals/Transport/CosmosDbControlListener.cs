using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class CosmosDbControlListener : IListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IReceiver _receiver;
    private readonly Task _receivingLoop;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly CosmosDbControlTransport _transport;

    public CosmosDbControlListener(CosmosDbControlTransport transport, CosmosDbControlEndpoint endpoint,
        IReceiver receiver, ILogger<CosmosDbControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _receiver = receiver;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _receivingLoop = Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds());

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await pollForMessagesAsync();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to poll for messages from the CosmosDb control queue");
                }

                await Task.Delay(1.Seconds());
            }
        }, _cancellation.Token);

        Address = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync();
#else
        _cancellation.Cancel();
#endif
        _receivingLoop.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync();
#else
        _cancellation.Cancel();
#endif
        if (_receivingLoop != null)
        {
            await _receivingLoop;
            _receivingLoop.Dispose();
        }
    }

    private async Task pollForMessagesAsync()
    {
        // Delete expired messages first
        await _transport.DeleteExpiredMessagesAsync(_cancellation.Token);

        // Poll for messages targeting this node
        var nodeId = _transport.Options.UniqueNodeId;
        var partitionKey = ControlMessage.PartitionKeyFor(nodeId);

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.docType = @docType AND c.nodeId = @nodeId")
            .WithParameter("@docType", DocumentTypes.ControlMessage)
            .WithParameter("@nodeId", nodeId.ToString());

        var envelopes = new List<Envelope>();

        using var iterator = _transport.Container.GetItemQueryIterator<ControlMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(_cancellation.Token);
            foreach (var msg in response)
            {
                var envelope = EnvelopeSerializer.Deserialize(msg.Body);
                envelopes.Add(envelope);
            }
        }

        if (envelopes.Count > 0)
        {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
            await _transport.DeleteEnvelopesAsync(envelopes, _cancellation.Token);
        }
    }

    private async Task deleteEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            var partitionKey = ControlMessage.PartitionKeyFor(_transport.Options.UniqueNodeId);
            await _transport.Container.DeleteItemAsync<ControlMessage>(
                envelope.Id.ToString(),
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted
        }
    }
}
