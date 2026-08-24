using Google.Cloud.PubSub.V1;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubListener : PubsubListener
{
    public InlinePubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime,
        PubsubClientSet clients
    ) : base(endpoint, transport, receiver, runtime, clients)
    {

    }

    public override async Task StartAsync()
    {
        // This listener used to pass no Settings at all, so it silently ran on the SDK defaults and ignored
        // whatever the user had configured through ConfigureListener(). The defaults happen to match
        // PubsubClientOptions' own defaults, so setting them explicitly is not a behavioural change -- but it
        // does mean MaxOutstandingMessages and, for GH-4066, MaxTotalAckExtension are now honoured on the very
        // endpoint mode where the subscriber callback is held for the entire handler pipeline.
        await listenForMessagesAsync(() => listenWithSubscriberAsync(new SubscriberClientBuilder
        {
            SubscriptionName = ListeningSubscriptionName,
            EmulatorDetection = _clients.EmulatorDetection,
            Settings = buildSubscriberSettings()
        }));
    }
}
