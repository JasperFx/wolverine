using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubListener : PubsubListener {
    public InlinePubsubListener(
        PubsubSubscription endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime) { }

    public override Task StartAsync() => listenForMessagesAsync(async () => {
        if (_transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        var response = await _transport.SubscriberApiClient.PullAsync(
            _endpoint.Name,
            maxMessages: 1,
            _cancellation.Token
        );

        await handleMessagesAsync(response.ReceivedMessages);
    });
}
