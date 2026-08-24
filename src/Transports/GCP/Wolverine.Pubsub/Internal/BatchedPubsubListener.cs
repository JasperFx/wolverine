using Google.Cloud.PubSub.V1;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class BatchedPubsubListener : PubsubListener
{
    public BatchedPubsubListener(
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
        await listenForMessagesAsync(() => listenWithSubscriberAsync(new SubscriberClientBuilder
        {
            SubscriptionName = ListeningSubscriptionName,
            EmulatorDetection = _clients.EmulatorDetection,
            Settings = buildSubscriberSettings()
        }));
    }
}
