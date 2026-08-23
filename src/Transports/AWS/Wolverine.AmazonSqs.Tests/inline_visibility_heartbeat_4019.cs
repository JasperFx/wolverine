using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-4019, against LocalStack. An inline handler that outlives the queue's visibility timeout. With
/// the heartbeat the message executes once; without it SQS redelivers mid-execution and a second
/// listener runs it again. The second test pins today's behavior on purpose, so the feature cannot
/// silently stop doing anything without a test going red.
/// </summary>
public class inline_visibility_heartbeat_4019
{
    // Short enough to keep the tests quick, long enough that LocalStack's second-granularity
    // visibility clock is not the thing being measured
    private const int VisibilityTimeoutSeconds = 2;
    private static readonly TimeSpan ObservationWindow = 14.Seconds();

    private static async Task<IHost> startHost(string queueName, bool extendVisibility)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                var listener = opts.ListenToSqsQueue(queueName, q =>
                    {
                        q.VisibilityTimeout = VisibilityTimeoutSeconds;
                        q.WaitTimeSeconds = 1;
                    })
                    .ProcessInline()
                    // The second listener is what picks up a redelivered copy while the first is still
                    // running the handler
                    .ListenerCount(2);

                if (extendVisibility)
                {
                    listener.ExtendVisibilityWhileHandling();
                }

                opts.PublishAllMessages().ToSqsQueue(queueName).SendInline();
            }).StartAsync();
    }

    private static async Task<int> executionsAfterObservationWindow(IHost host, Guid id)
    {
        await host.MessageBus().SendAsync(new SlowSqsMessage(id));
        await Task.Delay(ObservationWindow, TestContext.Current.CancellationToken);
        return SlowSqsMessageTracker.ExecutionsOf(id);
    }

    [Fact]
    public async Task with_the_heartbeat_a_handler_longer_than_the_visibility_timeout_executes_once()
    {
        var queueName = "heartbeat-on-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await startHost(queueName, extendVisibility: true);
        try
        {
            var id = Guid.NewGuid();
            var executions = await executionsAfterObservationWindow(host, id);

            executions.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task without_the_heartbeat_the_message_is_redelivered_and_executed_again_mid_handler()
    {
        var queueName = "heartbeat-off-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await startHost(queueName, extendVisibility: false);
        try
        {
            var id = Guid.NewGuid();
            var executions = await executionsAfterObservationWindow(host, id);

            // This is the defect. The first execution is still running at the 2 second mark when SQS
            // makes the message visible again, and the other listener starts it over.
            executions.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void the_heartbeat_is_only_built_for_inline_endpoints()
    {
        var transport = new AmazonSqsTransport();

        var buffered = transport.Queues["buffered"];
        buffered.ExtendVisibilityWhileHandling = true;
        buffered.Mode.ShouldNotBe(EndpointMode.Inline);

        var inline = transport.Queues["inline"];
        inline.ExtendVisibilityWhileHandling = true;
        inline.Mode = EndpointMode.Inline;

        SqsListener.ShouldExtendVisibility(buffered).ShouldBeFalse();
        SqsListener.ShouldExtendVisibility(inline).ShouldBeTrue();

        inline.ExtendVisibilityWhileHandling = false;
        SqsListener.ShouldExtendVisibility(inline).ShouldBeFalse();
    }
}

public record SlowSqsMessage(Guid Id);

public static class SlowSqsMessageTracker
{
    private static readonly ConcurrentDictionary<Guid, int> Executions = new();

    public static void Record(Guid id)
    {
        Executions.AddOrUpdate(id, 1, (_, count) => count + 1);
    }

    public static int ExecutionsOf(Guid id)
    {
        return Executions.GetValueOrDefault(id);
    }
}

public static class SlowSqsMessageHandler
{
    public static async Task Handle(SlowSqsMessage message)
    {
        // Counted at the start, so a second delivery that begins while the first is still running
        // shows up inside the observation window
        SlowSqsMessageTracker.Record(message.Id);
        await Task.Delay(6.Seconds());
    }
}
