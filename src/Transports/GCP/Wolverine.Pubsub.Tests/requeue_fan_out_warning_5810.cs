using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5810. Outside NativeAck a Pub/Sub requeue publishes a copy of the message to the topic, and Pub/Sub cannot
/// publish to one subscription, so every subscription on that topic receives the copy. Wolverine cannot see the
/// other services' subscriptions from its configuration, so it says so once per listener at startup, and
/// <c>AssertRequeuesDoNotFanOut()</c> turns that into a startup failure.
/// </summary>
/// <remarks>
/// The check runs over the compiled listener configuration, which Wolverine validates even when the external
/// transports are stubbed, so none of these need the emulator.
/// </remarks>
public class requeue_fan_out_warning_5810
{
    private static async Task<RecordingLoggerProvider> startAsync(Action<WolverineOptions> configure)
    {
        var recorder = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(recorder))
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        return recorder;
    }

    private static string[] fanOutWarnings(RecordingLoggerProvider recorder)
    {
        return recorder.Warnings.Where(x => x.Contains("receives the copy again")).ToArray();
    }

    [Fact]
    public async Task a_buffered_listener_warns_that_a_requeue_reaches_every_subscription_on_the_topic()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("fanout5810buffered").BufferedInMemory();
        });

        var warning = fanOutWarnings(recorder).ShouldHaveSingleItem();
        warning.ShouldContain("topic 'fanout5810buffered'");
        warning.ShouldContain("subscription 'fanout5810buffered'");
        warning.ShouldContain("ProcessInParallelWithNativeAcks()");
    }

    [Fact]
    public async Task an_inline_listener_warns_too()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("fanout5810inline").ProcessInline();
        });

        fanOutWarnings(recorder).ShouldHaveSingleItem().ShouldContain("topic 'fanout5810inline'");
    }

    [Fact]
    public async Task warns_once_per_listener_however_many_listeners_it_runs()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("fanout5810count").ListenerCount(3);
        });

        fanOutWarnings(recorder).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_native_ack_listener_does_not_warn()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("fanout5810nativeack").ProcessInParallelWithNativeAcks();
        });

        fanOutWarnings(recorder).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_topic_that_is_only_published_to_does_not_warn()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.PublishAllMessages().ToPubsubTopic("fanout5810publishonly");
        });

        fanOutWarnings(recorder).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_per_node_subscription_says_the_other_nodes_receive_it_too()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine");
            opts.ListenToPubsubTopic("fanout5810pernode").SubscriptionPerNode();
        });

        fanOutWarnings(recorder).ShouldHaveSingleItem().ShouldContain("SubscriptionPerNode()");
    }

    [Fact]
    public async Task the_opt_in_refuses_to_start_instead_of_warning()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            await startAsync(opts =>
            {
                opts.UsePubsub("wolverine").AssertRequeuesDoNotFanOut();
                opts.ListenToPubsubTopic("fanout5810assert");
            });
        });

        ex.Message.ShouldContain("topic 'fanout5810assert'");
        ex.Message.ShouldContain("AssertRequeuesDoNotFanOut()");
    }

    [Fact]
    public async Task the_opt_in_starts_a_native_ack_listener_without_a_word()
    {
        var recorder = await startAsync(opts =>
        {
            opts.UsePubsub("wolverine").AssertRequeuesDoNotFanOut();
            opts.ListenToPubsubTopic("fanout5810assertnativeack").ProcessInParallelWithNativeAcks();
        });

        fanOutWarnings(recorder).ShouldBeEmpty();
    }
}
