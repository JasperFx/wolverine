using System.Collections.Concurrent;
using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests.Bugs;

/// <summary>
/// GH-5842. With an <c>IdentifierPrefix</c>, <c>ListenToPubsubSubscription("S")</c> used to listen to "{prefix}.S",
/// a subscription that doesn't exist, instead of the existing subscription S.
/// </summary>
public class Bug_5842_existing_subscription_with_a_prefix
{
    [Fact]
    public async Task listens_to_the_existing_subscription_by_its_exact_name_when_a_prefix_is_set()
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);

        var runId = Guid.NewGuid().ToString("N");
        var topic = new TopicName("wolverine", $"gh5842-topic-{runId}");
        var subscription = new SubscriptionName("wolverine", $"gh5842-sub-{runId}");

        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        await publisher.CreateTopicAsync(topic);
        await subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = subscription, TopicAsTopicName = topic, AckDeadlineSeconds = 10
        });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().PrefixIdentifiers("dev");

                opts.ListenToPubsubSubscription(subscription.SubscriptionId)
                    .DefaultIncomingMessage<PrefixedExistingSubscriptionPing>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PrefixedExistingSubscriptionPingHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        await publisher.PublishAsync(topic, [
            new PubsubMessage
            {
                Data = ByteString.CopyFrom(
                    JsonSerializer.SerializeToUtf8Bytes(new PrefixedExistingSubscriptionPing(runId))),
                Attributes = { ["content-type"] = "application/json" }
            }
        ]);

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (!PrefixedExistingSubscriptionPingHandler.Handled.ContainsKey(runId) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        PrefixedExistingSubscriptionPingHandler.Handled.ContainsKey(runId)
            .ShouldBeTrue($"the message on subscription {subscription.SubscriptionId} was never handled");
    }
}

public record PrefixedExistingSubscriptionPing(string RunId);

public static class PrefixedExistingSubscriptionPingHandler
{
    public static readonly ConcurrentDictionary<string, bool> Handled = new();

    public static void Handle(PrefixedExistingSubscriptionPing ping)
    {
        Handled[ping.RunId] = true;
    }
}
