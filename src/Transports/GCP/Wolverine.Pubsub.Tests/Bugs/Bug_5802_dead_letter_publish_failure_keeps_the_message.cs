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
/// GH-5802. An Inline endpoint whose dead-letter topic is unavailable used to ack the original as soon as the first
/// dead-letter publish failed, so once the background retries ran out the message was gone from both the
/// subscription and the dead-letter topic. Now the original is nacked and Pub/Sub keeps redelivering it, and the
/// message reaches the dead-letter topic once that topic is back. GH-5843 is the same gap on a NativeAck endpoint.
/// </summary>
public class Bug_5802_dead_letter_publish_failure_keeps_the_message
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_message_is_dead_lettered_once_the_dead_letter_topic_comes_back(bool nativeAck)
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        var runId = Guid.NewGuid().ToString("N");
        var topicName = $"gh5802-{runId}";
        var deadLetterName = $"gh5802-dlq-{runId}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision();

                var listener = opts.ListenToPubsubTopic(topicName)
                    .DefaultIncomingMessage<PoisonPing5802>()
                    .ConfigureDeadLettering(deadLetterName);

                if (nativeAck)
                {
                    listener.ProcessInParallelWithNativeAcks();
                }
                else
                {
                    listener.ProcessInline();
                }

                opts.Policies.OnException<PoisonFailure5802>().MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PoisonPing5802Handler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        var deadLetterTopic = new TopicName("wolverine", deadLetterName);

        // The dead-letter topic goes away after Wolverine provisioned it
        await publisher.DeleteTopicAsync(deadLetterTopic);

        await publisher.PublishAsync(new TopicName("wolverine", topicName), [
            new PubsubMessage
            {
                Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new PoisonPing5802(runId))),
                Attributes = { ["content-type"] = "application/json" }
            }
        ]);

        // Fails, can't be dead lettered, and fails again on a redelivery: the original was not acked
        await PoisonPing5802Handler.WaitForAttemptsAsync(runId, 2);

        await publisher.CreateTopicAsync(deadLetterTopic);
        var observer = new SubscriptionName("wolverine", $"gh5802-observer-{runId}");
        await subscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = observer,
            TopicAsTopicName = deadLetterTopic
        });

        var deadLettered = new List<PubsubMessage>();
        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while (deadLettered.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            var pulled = await subscriber.PullAsync(new PullRequest
            {
                SubscriptionAsSubscriptionName = observer,
                MaxMessages = 10
            });

            deadLettered.AddRange(pulled.ReceivedMessages.Select(x => x.Message));

            if (pulled.ReceivedMessages.Any())
            {
                await subscriber.AcknowledgeAsync(observer, pulled.ReceivedMessages.Select(x => x.AckId));
            }
        }

        deadLettered.ShouldNotBeEmpty("the message was lost instead of reaching the dead-letter topic");
        deadLettered.ShouldAllBe(x => JsonSerializer.Deserialize<PoisonPing5802>(x.Data.ToByteArray(),
            (JsonSerializerOptions?)null)!.RunId == runId);
    }
}

public record PoisonPing5802(string RunId);

public class PoisonFailure5802() : Exception("always fails");

public static class PoisonPing5802Handler
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new();

    public static void Handle(PoisonPing5802 ping)
    {
        _attempts.AddOrUpdate(ping.RunId, 1, (_, n) => n + 1);
        throw new PoisonFailure5802();
    }

    public static async Task WaitForAttemptsAsync(string runId, int attempts)
    {
        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while (_attempts.GetValueOrDefault(runId) < attempts)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"{runId} was handled {_attempts.GetValueOrDefault(runId)} time(s), never redelivered");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
