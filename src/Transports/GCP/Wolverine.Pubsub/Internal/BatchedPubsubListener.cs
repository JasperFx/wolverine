using Google.Api.Gax.Grpc;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public class BatchedPubsubListener : PubsubListener {
    public BatchedPubsubListener(
        PubsubSubscription endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime) { }

    public override async Task StartAsync() {
        if (_transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        using var streamingPull = _transport.SubscriberApiClient.StreamingPull(CallSettings.FromCancellationToken(_cancellation.Token));

        await streamingPull.WriteAsync(new() {
            SubscriptionAsSubscriptionName = _endpoint.Name,
            StreamAckDeadlineSeconds = 20,
            MaxOutstandingMessages = _endpoint.PubsubOptions.MaxOutstandingMessages,
            MaxOutstandingBytes = _endpoint.PubsubOptions.MaxOutstandingByteCount,
        });

        await using var stream = streamingPull.GetResponseStream();

        _complete = new RetryBlock<PubsubEnvelope[]>(
            async (envelopes, _) => {
                await streamingPull.WriteAsync(new() { AckIds = { envelopes.Select(x => x.AckId).ToArray() } });
            },
            _logger,
            _cancellation.Token
        );

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
