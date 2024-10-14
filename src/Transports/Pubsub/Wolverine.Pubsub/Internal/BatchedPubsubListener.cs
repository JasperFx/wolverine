using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util.Dataflow;

namespace Wolverine.Pubsub.Internal;

public class BatchedPubsubListener : PubsubListener2 {
    public BatchedPubsubListener(
        PubsubSubscription endpoint,
        ILogger logger,
        IReceiver receiver,
        ISender requeuer,
        IIncomingMapper<PubsubMessage> mapper
    ) : base(endpoint, logger, receiver, requeuer, mapper) { }

    public override async Task StartAsync() {
        if (_endpoint.Transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        using var streamingPull = _endpoint.Transport.SubscriberApiClient.StreamingPull(CallSettings.FromCancellationToken(_cancellation.Token));

        await streamingPull.WriteAsync(new() {
            SubscriptionAsSubscriptionName = _endpoint.SubscriptionName,
            StreamAckDeadlineSeconds = 20,
            MaxOutstandingMessages = _endpoint.Options.MaxOutstandingMessages ?? 0,
            MaxOutstandingBytes = _endpoint.Options.MaxOutstandingByteCount ?? 0,
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
                    var response = stream.Current;

                    await handleMessagesAsync(
                        response.ReceivedMessages,
                        (envelopes) => _complete.PostAsync(envelopes.ToArray())
                    );
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
