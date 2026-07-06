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
        await listenForMessagesAsync(async () =>
        {
            var subscriptionName = ListeningSubscriptionName;
            var subscriberBuilder = new SubscriberClientBuilder
            {
                SubscriptionName = subscriptionName,
                EmulatorDetection = _clients.EmulatorDetection,
            };
            if (_clients.ConfigureSubscriberClientBuilder != null)
                await _clients.ConfigureSubscriberClientBuilder(subscriberBuilder);
            await using SubscriberClient subscriber = await subscriberBuilder.BuildAsync();
            var ctRegistration = _cancellation.Token.Register(() => subscriber.StopAsync(CancellationToken.None));
            try
            {
                await subscriber.StartAsync(async (PubsubMessage message, CancellationToken cancel) =>
                {
                    var success = await handleMessageAsync(message);
                    return success ? SubscriberClient.Reply.Ack : SubscriberClient.Reply.Nack;
                });
            }
            finally
            {
                ctRegistration.Unregister();
                await subscriber.StopAsync(CancellationToken.None);
            }
        });
    }
}