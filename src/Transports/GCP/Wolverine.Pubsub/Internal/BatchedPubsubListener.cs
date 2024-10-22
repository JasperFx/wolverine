using Google.Api.Gax.Grpc;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class BatchedPubsubListener : PubsubListener {
    public BatchedPubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime) { }

    public override async Task StartAsync() {
        if (_transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        using var streamingPull = _transport.SubscriberApiClient.StreamingPull(CallSettings.FromCancellationToken(_cancellation.Token));

        await streamingPull.WriteAsync(new() {
            SubscriptionAsSubscriptionName = _endpoint.Server.Subscription.Name,
            StreamAckDeadlineSeconds = 20,
            MaxOutstandingMessages = _endpoint.Client.MaxOutstandingMessages,
            MaxOutstandingBytes = _endpoint.Client.MaxOutstandingByteCount,
        });

        await using var stream = streamingPull.GetResponseStream();

        _acknowledge = ackIds => streamingPull.WriteAsync(new() {
            AckIds = { ackIds }
        });

        try {
            await listenForMessagesAsync(async () => {
                while (await stream.MoveNextAsync(_cancellation.Token)) {
                    await handleMessagesAsync(stream.Current.ReceivedMessages);
                }
            });
        }
        finally {
            try {
                await streamingPull.WriteCompleteAsync();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "{Uri}: Error while completing the Google Cloud Pub/Sub streaming pull.", _endpoint.Uri);
            }
        }
    }
}
