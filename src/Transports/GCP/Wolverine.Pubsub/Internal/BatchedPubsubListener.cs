using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
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
        if (_transport.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        // Create a high-level SubscriberClient for receiving messages which may
        // use multiple underlying streaming pull connections.
        var subscriberClientBuilder = new SubscriberClientBuilder()
        {
            EmulatorDetection = _transport.EmulatorDetection,
            SubscriptionName = _endpoint.Server.Subscription.Name,
            Settings = new SubscriberClient.Settings
            {
                AckDeadline = TimeSpan.FromSeconds(20),
                MaxTotalAckExtension = TimeSpan.FromMinutes(10),
                FlowControlSettings = new FlowControlSettings(
                    _endpoint.Client.MaxOutstandingMessages,
                    _endpoint.Client.MaxOutstandingByteCount
                )
            }
        };

        await using var subscriberClient = await subscriberClientBuilder.BuildAsync();

        try
        {
            // Start the subscriber and capture the lifetime task
            var subscriberLifetime = subscriberClient.StartAsync(async (msg, ct) =>
            {
                await handleMessagesAsync(msg);
                return SubscriberClient.Reply.Ack;
            });

            // Wait for whatever condition you have for running (your helper can return the lifetime task)
            await listenForMessagesAsync(() => subscriberLifetime);

            // When listenForMessagesAsync returns, request a graceful stop
        }
        finally
        {
            try
            {
                await subscriberClient.StopAsync(TimeSpan.FromSeconds(15));
                // Ensure the StartAsync task has completed and observe any exceptions
                // (if subscriberLifetime had an exception it will be rethrown here)
                // Note: if you need the variable here, capture it in an outer scope.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Uri}: Error while completing the Google Cloud Platform Pub/Sub streaming pull.",
                    _endpoint.Uri);
            }
        }
    }
}
