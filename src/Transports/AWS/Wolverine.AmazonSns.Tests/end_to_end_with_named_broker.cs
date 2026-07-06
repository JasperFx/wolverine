using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

// Named-broker (GH-3305) end-to-end coverage. The SNS side is publish-only: we publish to an SNS topic ON A NAMED
// broker, whose standalone paired SQS client provisions the SNS->SQS subscription, and a Wolverine SQS listener on
// the default broker consumes it. Proves the named SNS topic carries the broker name as its URI scheme (not the
// hard-coded "sns"), and that publish -> subscribed SQS queue -> handler works on the named broker.
//
// NOTE: the receiving SQS listener runs on the DEFAULT SQS broker, not a same-named SQS broker. A named SNS broker
// and a same-named SQS broker cannot coexist because the TransportCollection keys transports by Protocol (== the
// broker name); the named SNS holds that key. The SNS-side subscription provisioning uses a standalone paired SQS
// client seeded from the SNS connection, so it still targets the same LocalStack account.
//
// Guarded to skip when LocalStack/Docker is unavailable.
public class end_to_end_with_named_broker : IAsyncLifetime
{
    private readonly BrokerName theName = new("other");
    private bool _skip;

    public async Task InitializeAsync()
    {
        _skip = !await SnsTestingExtensions.IsLocalStackAvailable();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void named_broker_topic_uri_uses_the_broker_name_as_scheme()
    {
        // Pure wiring assertion (no broker needed): the topic URI scheme must be the broker Protocol (== name),
        // NOT the hard-coded "sns" literal, or findEndpointByUri would mismatch on a named broker.
        var options = new WolverineOptions();
        options.AddNamedAmazonSnsBroker(theName);

        var transport = options.AmazonSnsTransport(theName);
        transport.Protocol.ShouldBe("other");

        var topic = transport.Topics["colors"];
        topic.Uri.Scheme.ShouldBe("other");
        topic.Uri.ShouldBe(new Uri("other://colors"));
    }

    [Fact]
    public async Task publish_to_named_sns_topic_and_receive_via_named_sqs_subscription()
    {
        if (_skip) return;

        var topic = $"named-{Guid.NewGuid():N}";
        var queue = $"named-{Guid.NewGuid():N}";

        NamedBrokerHandler.Received = new();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Receiving side runs on the DEFAULT SQS broker (see the class note on the protocol-key constraint).
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToSqsQueue(queue).ReceiveSnsTopicMessage();

                // Publishing side runs on the NAMED SNS broker.
                opts.UseAmazonSnsTransportLocallyAsNamedBroker(theName)
                    .AutoProvision();

                opts.PublishMessage<NamedBrokerMessage>()
                    .ToSnsTopicOnNamedBroker(theName, topic)
                    .SubscribeSqsQueue(queue);
            }).StartAsync();

        await host.MessageBus().PublishAsync(new NamedBrokerMessage("blue"));

        var received = await NamedBrokerHandler.Received.Task.TimeoutAfterAsync(30000);
        received.Color.ShouldBe("blue");
    }
}

public record NamedBrokerMessage(string Color);

public static class NamedBrokerHandler
{
    public static TaskCompletionSource<NamedBrokerMessage> Received { get; set; } = new();

    public static void Handle(NamedBrokerMessage message)
    {
        Received.TrySetResult(message);
    }
}

internal static class SnsTestingExtensions
{
    private const string ServiceUrl = "http://localhost:4566";

    public static async Task<bool> IsLocalStackAvailable()
    {
        try
        {
            using var client = new AmazonSQSClient(new Amazon.Runtime.BasicAWSCredentials("ignore", "ignore"),
                new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ListQueuesAsync(new ListQueuesRequest(), cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
