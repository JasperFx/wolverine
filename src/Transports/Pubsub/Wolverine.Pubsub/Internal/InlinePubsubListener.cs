using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubListener : PubsubListener2 {
    public InlinePubsubListener(
        PubsubSubscription endpoint,
        ILogger logger,
        IReceiver receiver,
        ISender requeuer,
        IIncomingMapper<PubsubMessage> mapper
    ) : base(endpoint, logger, receiver, requeuer, mapper) { }

    public override Task StartAsync() => listenForMessagesAsync(async () => {
        if (_endpoint.Transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        var response = await _endpoint.Transport.SubscriberApiClient.PullAsync(
            _endpoint.SubscriptionName,
            maxMessages: 1,
            _cancellation.Token
        );

        await handleMessagesAsync(
            response.ReceivedMessages,
            (envelopes) => _complete.PostAsync(envelopes.ToArray())
        );
    });
}
