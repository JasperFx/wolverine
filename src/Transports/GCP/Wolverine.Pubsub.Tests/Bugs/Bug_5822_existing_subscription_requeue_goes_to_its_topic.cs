using System.Collections.Concurrent;
using System.Text.Json;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.Pubsub.Tests.Bugs;

/// <summary>
/// GH-5822. <c>ListenToPubsubSubscription(subscriptionName)</c> listens on a subscription Wolverine did not create,
/// and the endpoint used to take the subscription's NAME as its topic. A requeue publishes a copy to the endpoint's
/// topic, so the copy went to a topic named after the subscription, which does not exist. Inline then fell back to
/// a Nack (GH-5771) and never published a copy at all; Buffered had already acked the original, so the message was
/// lost.
/// </summary>
public class Bug_5822_existing_subscription_requeue_goes_to_its_topic
{
    private static async Task<(PublisherServiceApiClient Publisher, SubscriberServiceApiClient Subscriber)> clientsAsync()
    {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);

        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        return (publisher, subscriber);
    }

    /// <summary>
    /// A topic, the existing subscription Wolverine listens on, and an observer subscription that sees every message
    /// published to the topic -- the original and any requeue copy.
    /// </summary>
    private static async Task<(TopicName Topic, SubscriptionName Existing, SubscriptionName Observer)> createTopologyAsync(
        PublisherServiceApiClient publisher, SubscriberServiceApiClient subscriber, string runId)
    {
        var topic = new TopicName("wolverine", $"gh5822-topic-{runId}");
        var existing = new SubscriptionName("wolverine", $"gh5822-sub-{runId}");
        var observer = new SubscriptionName("wolverine", $"gh5822-observer-{runId}");

        await publisher.CreateTopicAsync(topic);
        await subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = existing, TopicAsTopicName = topic, AckDeadlineSeconds = 10
        });
        await subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = observer, TopicAsTopicName = topic, AckDeadlineSeconds = 10
        });

        return (topic, existing, observer);
    }

    private static Task<IHost> startHostAsync(string subscriptionId, EndpointMode mode, string? explicitTopic = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting();

                var listener = opts.ListenToPubsubSubscription(subscriptionId)
                    .DefaultIncomingMessage<ExistingSubscriptionPing>();

                if (explicitTopic != null)
                {
                    listener.FromTopic(explicitTopic);
                }

                if (mode == EndpointMode.Inline)
                {
                    listener.ProcessInline();
                }
                else
                {
                    listener.BufferedInMemory();
                }

                opts.Policies.OnException<ExistingSubscriptionRequeueOnce>().Requeue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExistingSubscriptionPingHandler));
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<PubsubMessage>> observeAsync(SubscriberServiceApiClient subscriber,
        SubscriptionName observer, int expected)
    {
        var seen = new List<PubsubMessage>();
        var deadline = DateTimeOffset.UtcNow.Add(20.Seconds());
        while (seen.Count < expected && DateTimeOffset.UtcNow < deadline)
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

        // Give a stray extra copy a moment to show up as well
        var extra = await subscriber.PullAsync(new PullRequest
        {
            SubscriptionAsSubscriptionName = observer,
            MaxMessages = 10
        }, CallSettings.FromExpiration(Expiration.FromTimeout(TimeSpan.FromSeconds(3))));
        seen.AddRange(extra.ReceivedMessages.Select(x => x.Message));

        return seen;
    }

    [Theory]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.BufferedInMemory)]
    public async Task a_requeue_from_an_existing_subscription_republishes_to_the_subscriptions_topic(EndpointMode mode)
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        var runId = Guid.NewGuid().ToString("N");
        var (publisher, subscriber) = await clientsAsync();
        var (topic, existing, observer) = await createTopologyAsync(publisher, subscriber, runId);

        using var host = await startHostAsync(existing.SubscriptionId, mode);

        await publisher.PublishAsync(topic, [
            new PubsubMessage
            {
                Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new ExistingSubscriptionPing(runId))),
                Attributes = { ["content-type"] = "application/json" }
            }
        ]);

        // Handled after its requeue. Buffered used to lose the message here: the original was acked and the copy
        // could not be published
        await ExistingSubscriptionPingHandler.WaitForAsync(runId);

        // The copy went to the subscription's real topic. Inline used to Nack the original instead, because the
        // copy's publish to a topic named after the subscription failed
        var seen = await observeAsync(subscriber, observer, 2);
        seen.Count.ShouldBe(2, "the original and the requeue copy, both on the subscription's topic");
    }

    [Fact]
    public async Task an_explicit_topic_is_where_the_requeue_copy_goes()
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        var runId = Guid.NewGuid().ToString("N");
        var (publisher, subscriber) = await clientsAsync();
        var (topic, existing, observer) = await createTopologyAsync(publisher, subscriber, runId);

        using var host = await startHostAsync(existing.SubscriptionId, EndpointMode.BufferedInMemory, topic.TopicId);

        await publisher.PublishAsync(topic, [
            new PubsubMessage
            {
                Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new ExistingSubscriptionPing(runId))),
                Attributes = { ["content-type"] = "application/json" }
            }
        ]);

        await ExistingSubscriptionPingHandler.WaitForAsync(runId);

        var seen = await observeAsync(subscriber, observer, 2);
        seen.Count.ShouldBe(2, "the original and the requeue copy, both on the configured topic");
    }
}

public record ExistingSubscriptionPing(string RunId);

public class ExistingSubscriptionRequeueOnce() : Exception("fails the first time");

public static class ExistingSubscriptionPingHandler
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new();
    private static readonly ConcurrentDictionary<string, bool> _handled = new();

    public static void Handle(ExistingSubscriptionPing ping)
    {
        if (_attempts.AddOrUpdate(ping.RunId, 1, (_, n) => n + 1) == 1)
        {
            throw new ExistingSubscriptionRequeueOnce();
        }

        _handled[ping.RunId] = true;
    }

    public static async Task WaitForAsync(string runId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(45.Seconds());
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
