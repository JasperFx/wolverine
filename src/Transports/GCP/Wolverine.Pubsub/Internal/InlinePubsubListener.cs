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
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime)
    {

    }

    public override async Task StartAsync()
    {
        await listenForMessagesAsync(async () =>
        {
            var subscriptionName = _endpoint.Server.Subscription.Name;
            var subscriberBuilder = new SubscriberClientBuilder
            {
                SubscriptionName = subscriptionName,
                EmulatorDetection = _transport.EmulatorDetection,
            };
            if (_transport.ConfigureSubscriberClientBuilder != null)
                await _transport.ConfigureSubscriberClientBuilder(subscriberBuilder);
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