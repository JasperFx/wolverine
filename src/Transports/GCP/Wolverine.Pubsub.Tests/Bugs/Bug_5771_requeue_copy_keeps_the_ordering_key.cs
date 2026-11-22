using System.Collections.Concurrent;
using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.Pubsub.Tests.Bugs;

/// <summary>
/// GH-5771. A message from a publisher other than Wolverine carries its ordering key on the Pub/Sub message only,
/// never in Wolverine's <c>group-id</c> attribute. A requeue republishes a copy of it, and that copy used to go out
/// without any ordering key at all.
/// </summary>
public class Bug_5771_requeue_copy_keeps_the_ordering_key
{
    [Fact]
    public async Task an_interop_message_requeued_inline_is_republished_with_its_ordering_key()
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        var runId = Guid.NewGuid().ToString("N");
        var topicName = $"gh5771-{runId}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision();

                opts.ListenToPubsubTopic(topicName)
                    .DefaultIncomingMessage<OrderedPing5771>()
                    .ProcessInline();

                opts.Policies.OnException<RequeueOnce5771>().Requeue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(OrderedPing5771Handler));
            }).StartAsync(TestContext.Current.CancellationToken);

        // A second subscription on the topic sees every message published to it -- the original and the copy
        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        var observer = new SubscriptionName("wolverine", $"gh5771-observer-{runId}");
        await subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = observer,
            TopicAsTopicName = new TopicName("wolverine", topicName),
            EnableMessageOrdering = true
        });

        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        await publisher.PublishAsync(new TopicName("wolverine", topicName), [
            new PubsubMessage
            {
                Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new OrderedPing5771(runId))),
                Attributes = { ["content-type"] = "application/json" },
                OrderingKey = "customer-42"
            }
        ]);

        // Handled once after the requeue, so the copy arrived
        await OrderedPing5771Handler.WaitForAsync(runId);

        var seen = new List<PubsubMessage>();
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (seen.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            var pulled = await subscriber.PullAsync(new PullRequest
            {
                SubscriptionAsSubscriptionName = observer,
                MaxMessages = 10
            });

            seen.AddRange(pulled.ReceivedMessages.Select(x => x.Message));

            if (pulled.ReceivedMessages.Any())
            {
                await subscriber.AcknowledgeAsync(observer, pulled.ReceivedMessages.Select(x => x.AckId));
            }
        }

        seen.Count.ShouldBe(2, "the original and the requeue copy");
        seen.ShouldAllBe(x => x.OrderingKey == "customer-42");
    }
}

public record OrderedPing5771(string RunId);

public class RequeueOnce5771() : Exception("fails the first time");

public static class OrderedPing5771Handler
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new();
    private static readonly ConcurrentDictionary<string, bool> _handled = new();

    public static void Handle(OrderedPing5771 ping)
    {
        if (_attempts.AddOrUpdate(ping.RunId, 1, (_, n) => n + 1) == 1)
        {
            throw new RequeueOnce5771();
        }

        _handled[ping.RunId] = true;
    }

    public static async Task WaitForAsync(string runId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while (!_handled.ContainsKey(runId))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"{runId} was never handled after its requeue");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
