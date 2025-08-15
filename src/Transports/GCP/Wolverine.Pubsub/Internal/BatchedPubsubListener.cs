using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public class BatchedPubsubListener : PubsubListener
{
    public BatchedPubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime)
    {
    }

    public override async Task StartAsync()
    {
        if (_transport.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        using var streamingPull =
            _transport.SubscriberApiClient.StreamingPull(CallSettings.FromCancellationToken(_cancellation.Token));

        await streamingPull.WriteAsync(new StreamingPullRequest
        {
            SubscriptionAsSubscriptionName = _endpoint.Server.Subscription.Name,
            StreamAckDeadlineSeconds = 20,
            MaxOutstandingMessages = _endpoint.Client.MaxOutstandingMessages,
            MaxOutstandingBytes = _endpoint.Client.MaxOutstandingByteCount
        });

        await using var stream = streamingPull.GetResponseStream();

        _acknowledge = new RetryBlock<string[]>((ackIds, _) => streamingPull.WriteAsync(new StreamingPullRequest
        {
            AckIds = { ackIds }
        }), _logger, _runtime.Cancellation);

        try
        {
            await listenForMessagesAsync(async () =>
            {
                while (await stream.MoveNextAsync(_cancellation.Token))
                {
                    await handleMessagesAsync(stream.Current.ReceivedMessages);
                }
            });
        }
        finally
        {
            try
            {
                await streamingPull.WriteCompleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Uri}: Error while completing the Google Cloud Platform Pub/Sub streaming pull.",
                    _endpoint.Uri);
            }
        }
    }
}