using System.Diagnostics;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-4065. SubscriberClient.StopAsync() waits on in-flight message callbacks, and Wolverine's callback awaits
/// IReceiver.ReceivedAsync -- which on an EndpointMode.Inline endpoint runs the whole handler pipeline. The
/// listener used to stop with CancellationToken.None, so a single slow handler wedged listener teardown with
/// nothing bounding the wait at all.
///
/// These run against the real Google.Cloud.PubSub.V1 SubscriberClient (pointed at the emulator) with a callback
/// that never returns, which is the exact shape of the hang. Skip-guarded when the emulator is unavailable.
/// </summary>
public class listener_shutdown_4065 : IAsyncLifetime
{
    private static readonly Uri TheUri = new("pubsub://wolverine/shutdown");

    private bool _skip;

    public async ValueTask InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task stops_within_the_drain_budget_even_when_a_callback_never_returns()
    {
        if (_skip) return;

        // Never completed -- this callback models a handler that outlives shutdown and ignores cancellation
        var held = new TaskCompletionSource<SubscriberClient.Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = await provisionAsync();
        var subscriber = await new SubscriberClientBuilder
        {
            SubscriptionName = subscription,
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        _ = subscriber.StartAsync((_, _) =>
        {
            callbackEntered.TrySetResult();
            return held.Task;
        });

        await publishAsync(subscription);

        // Only meaningful if the callback is genuinely in flight when we stop
        await callbackEntered.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        var drainTimeout = 2.Seconds();
        var stopwatch = Stopwatch.StartNew();

        // WaitAsync so a regression FAILS the test instead of hanging the suite forever
        await PubsubListener
            .StopAndDisposeSubscriberAsync(subscriber, drainTimeout, NullLogger.Instance, TheUri)
            .WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

        stopwatch.Stop();

        // The bound is drainTimeout for the stop plus drainTimeout/2 for the disposal. Assert generously --
        // the point is that it RETURNS, not the exact number of milliseconds.
        stopwatch.Elapsed.ShouldBeLessThan(20.Seconds());

        held.TrySetResult(SubscriberClient.Reply.Nack);
    }

    [Fact]
    public async Task returns_promptly_when_there_is_nothing_in_flight()
    {
        if (_skip) return;

        var subscription = await provisionAsync();
        var subscriber = await new SubscriberClientBuilder
        {
            SubscriptionName = subscription,
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync(TestContext.Current.CancellationToken);

        _ = subscriber.StartAsync((_, _) => Task.FromResult(SubscriberClient.Reply.Ack));

        var stopwatch = Stopwatch.StartNew();

        await PubsubListener
            .StopAndDisposeSubscriberAsync(subscriber, 30.Seconds(), NullLogger.Instance, TheUri)
            .WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

        stopwatch.Stop();

        // An idle subscriber must not sit out the drain budget
        stopwatch.Elapsed.ShouldBeLessThan(15.Seconds());
    }

    private static async Task<SubscriptionName> provisionAsync()
    {
        var id = $"shutdown-{Guid.NewGuid():N}";

        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();
        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();

        var topicName = new TopicName("wolverine", id);
        var subscriptionName = new SubscriptionName("wolverine", id);

        try
        {
            await publisher.CreateTopicAsync(topicName);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }

        try
        {
            await subscriber.CreateSubscriptionAsync(subscriptionName, topicName, pushConfig: null,
                ackDeadlineSeconds: 60);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }

        return subscriptionName;
    }

    private static async Task publishAsync(SubscriptionName subscription)
    {
        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();

        await publisher.PublishAsync(new TopicName(subscription.ProjectId, subscription.SubscriptionId), [
            new PubsubMessage { Data = ByteString.CopyFromUtf8("hold me") }
        ]);
    }
}
