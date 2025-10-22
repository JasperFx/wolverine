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
        IWolverineRuntime runtime
    ) : base(endpoint, transport, receiver, runtime)
    {
    }

    public override async Task StartAsync()
    {
        await listenForMessagesAsync(async () =>
        {
            var subscriptionName = _endpoint.Server.Subscription.Name;
            await using SubscriberClient subscriber = await new SubscriberClientBuilder
            {
                SubscriptionName = subscriptionName,
                EmulatorDetection = _transport.EmulatorDetection,
                Settings = new()
                {
                    // https://cloud.google.com/dotnet/docs/reference/Google.Cloud.PubSub.V1/latest/Google.Cloud.PubSub.V1.SubscriberClient.Settings#Google_Cloud_PubSub_V1_SubscriberClient_Settings_FlowControlSettings
                    // Remarks: Flow control uses these settings for two purposes: fetching messages to process, and processing them.
                    // In terms of fetching messages, a single SubscriberClient creates multiple instances of SubscriberServiceApiClient, and each will observe the flow control settings independently
                    FlowControlSettings = new(_endpoint.Client.MaxOutstandingMessages, _endpoint.Client.MaxOutstandingByteCount),
                }
            }.BuildAsync();
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