using System.Text;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5781. Since GH-5123 a requeue publishes its copy before it acks the original, but with publisher confirms off
/// (the default) "published" only meant "written to the socket". A copy the broker refused or could not route was
/// dropped without a word, and the original was acked anyway: the message was gone. The requeue copy now goes out on
/// a channel of its own with publisher confirms, and as a mandatory publish, so the original is only acked once the
/// broker has the copy. Ordinary sends keep whatever confirm settings the application chose.
/// </summary>
public class requeue_copy_is_confirmed_5781
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_copy_the_broker_refuses_does_not_get_the_original_acked()
    {
        var queueName = RabbitTesting.NextQueueName();
        ConfirmedRequeueHandler.Reset(queueName, RequeueSabotage.FillTheQueue);

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.LocalRoutingConventionDisabled = true;
                    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishMessage<ConfirmedRequeueMessage>().ToRabbitQueue(queueName).SendInline();

                    // One ready message fills the queue, and a full queue refuses a publish instead of dropping its
                    // oldest message. The delivery being handled is unacked, so it does not count towards the limit.
                    opts.ListenToRabbitQueue(queueName, q =>
                        {
                            q.Arguments["x-max-length"] = 1;
                            q.Arguments["x-overflow"] = "reject-publish";
                        })
                        .PreFetchCount(1)
                        .ProcessInline();
                }).StartAsync(Token);

            await host.MessageBus().PublishAsync(new ConfirmedRequeueMessage(false));

            var outcome = await ConfirmedRequeueHandler.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            outcome.Threw.ShouldBeTrue("the broker refused the requeue copy, so the requeue has to fail");
            outcome.Acknowledged.ShouldBeFalse("the copy never reached the queue, so the original is the only copy left");

            await host.StopAsync(Token);
        }
        finally
        {
            await ConfirmedRequeueHandler.DeleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task a_copy_the_broker_cannot_route_is_not_counted_as_published()
    {
        var queueName = RabbitTesting.NextQueueName();
        ConfirmedRequeueHandler.Reset(queueName, RequeueSabotage.DeleteTheQueue);

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.LocalRoutingConventionDisabled = true;
                    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishMessage<ConfirmedRequeueMessage>().ToRabbitQueue(queueName).SendInline();
                    opts.ListenToRabbitQueue(queueName).PreFetchCount(1).ProcessInline();
                }).StartAsync(Token);

            await host.MessageBus().PublishAsync(new ConfirmedRequeueMessage(false));

            var outcome = await ConfirmedRequeueHandler.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            // Without mandatory, the broker confirms an unroutable message and drops it
            outcome.Threw.ShouldBeTrue("the requeue copy has no queue to go to, so the requeue has to fail");
            outcome.Acknowledged.ShouldBeFalse();

            await host.StopAsync(Token);
        }
        finally
        {
            await ConfirmedRequeueHandler.DeleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task ordinary_sends_keep_the_application_confirm_settings()
    {
        var queueName = RabbitTesting.NextQueueName();
        ConfirmedRequeueHandler.Reset(queueName, RequeueSabotage.None);

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.LocalRoutingConventionDisabled = true;
                    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishMessage<ConfirmedRequeueMessage>().ToRabbitQueue(queueName).SendInline();
                    opts.ListenToRabbitQueue(queueName).PreFetchCount(1).ProcessInline();
                }).StartAsync(Token);

            await host.MessageBus().PublishAsync(new ConfirmedRequeueMessage(false));

            var outcome = await ConfirmedRequeueHandler.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            outcome.Threw.ShouldBeFalse();
            outcome.Acknowledged.ShouldBeTrue();

            // The copy arrives and is handled
            await ConfirmedRequeueHandler.CopyHandled.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            var queue = host.GetRuntime().Options.RabbitMqTransport().Queues[queueName];
            var sender = queue.ResolveSender(host.GetRuntime()).ShouldBeOfType<RabbitMqSender>();
            sender.Channel.ShouldNotBeNull();
            (await sender.Channel.GetNextPublishSequenceNumberAsync(Token)).ShouldBe(0ul,
                "the ordinary sending channel was switched to publisher confirms");

            await host.StopAsync(Token);
        }
        finally
        {
            await ConfirmedRequeueHandler.DeleteQueueAsync(queueName);
        }
    }
}

public record ConfirmedRequeueMessage(bool Filler);

public enum RequeueSabotage
{
    None,
    FillTheQueue,
    DeleteTheQueue
}

public static class ConfirmedRequeueHandler
{
    private static int _deliveries;
    private static RequeueSabotage _sabotage;
    private static string _queueName = "";

    public static TaskCompletionSource<(bool Threw, bool Acknowledged)> Requeued { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource CopyHandled { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset(string queueName, RequeueSabotage sabotage)
    {
        _queueName = queueName;
        _sabotage = sabotage;
        _deliveries = 0;
        Requeued = new TaskCompletionSource<(bool, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        CopyHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(ConfirmedRequeueMessage message, Envelope envelope)
    {
        if (envelope is not RabbitMqEnvelope rabbit || message.Filler) return;

        if (Interlocked.Increment(ref _deliveries) > 1)
        {
            CopyHandled.TrySetResult();
            return;
        }

        switch (_sabotage)
        {
            case RequeueSabotage.FillTheQueue:
                await fillTheQueueAsync();
                break;
            case RequeueSabotage.DeleteTheQueue:
                await DeleteQueueAsync(_queueName);
                break;
        }

        var threw = false;
        try
        {
            await rabbit.RabbitMqListener.TryRequeueAsync(rabbit);
        }
        catch (Exception)
        {
            threw = true;
        }

        Requeued.TrySetResult((threw, rabbit.Acknowledged));
    }

    // Publishes one harmless message straight to the queue, and waits for the broker to confirm it is there
    private static async Task fillTheQueueAsync()
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));

        var props = new BasicProperties
        {
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>
            {
                [EnvelopeConstants.MessageTypeKey] = typeof(ConfirmedRequeueMessage).ToMessageTypeName(),
                [EnvelopeConstants.IdKey] = Guid.NewGuid().ToString()
            }
        };

        await channel.BasicPublishAsync("", _queueName, true, props, Encoding.UTF8.GetBytes("{\"Filler\":true}"));

        (await channel.QueueDeclarePassiveAsync(_queueName)).MessageCount.ShouldBe(1u);
    }

    public static async Task DeleteQueueAsync(string queueName)
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queueName, false, false);
    }
}
