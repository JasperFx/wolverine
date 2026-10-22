using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// N1. <c>RabbitMqListener.TryRequeueAsync</c> nacked the original delivery with <c>requeue: false</c> and only then
/// published the copy that replaces it. A nack without requeue is exactly what routes a delivery through the queue's
/// <c>x-dead-letter-exchange</c>, so on a queue with native dead lettering -- Wolverine's default -- every requeue
/// put the original in the dead letter queue AND a copy back on the queue. And a copy that could not be published
/// left the message nowhere but the dead letter queue: the same ack-before-publish hazard GH-5123 fixed for a
/// deferral. A requeue now publishes the copy first and then acks the original, which never dead letters it.
/// </summary>
public class requeue_does_not_dead_letter_the_original
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<IHost> hostWith(string queueName)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<RequeueMessage>().ToRabbitQueue(queueName).SendInline();
                opts.ListenToRabbitQueue(queueName).ProcessInline()
                    .DeadLetterQueueing(new DeadLetterQueue(queueName + "_dlq"));
            }).StartAsync(Token);
    }

    [Fact]
    public async Task a_requeue_does_not_dead_letter_the_original_delivery()
    {
        var queueName = RabbitTesting.NextQueueName();
        RequeueHandler.Reset(queueName, breakTheCopy: false);

        using var host = await hostWith(queueName);

        await host.MessageBus().PublishAsync(new RequeueMessage());

        // The copy arrives and is handled
        await RequeueHandler.CopyHandled.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var outcome = await RequeueHandler.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        outcome.Threw.ShouldBeFalse();

        (await RequeueHandler.CountAsync(queueName + "_dlq")).ShouldBe(0u,
            "the original was dead lettered alongside the copy that replaced it");

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task a_requeue_whose_copy_cannot_be_published_does_not_settle_the_original()
    {
        var queueName = RabbitTesting.NextQueueName();
        RequeueHandler.Reset(queueName, breakTheCopy: true);

        using var host = await hostWith(queueName);

        await host.MessageBus().PublishAsync(new RequeueMessage());

        var outcome = await RequeueHandler.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        outcome.Threw.ShouldBeTrue("the copy cannot be published, so the requeue has to fail");
        outcome.Acknowledged.ShouldBeFalse("the original is the only copy of the message left");
        outcome.DeadLettered.ShouldBe(0u, "the original was settled into the dead letter queue before the copy existed");

        await host.StopAsync(Token);
    }
}

public record RequeueMessage;

public static class RequeueHandler
{
    private static int _deliveries;
    private static bool _breakTheCopy;
    private static string _queueName = "";

    public static TaskCompletionSource<(bool Threw, bool Acknowledged, uint DeadLettered)> Requeued
    {
        get;
        private set;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource CopyHandled { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset(string queueName, bool breakTheCopy)
    {
        _queueName = queueName;
        _breakTheCopy = breakTheCopy;
        _deliveries = 0;
        Requeued = new TaskCompletionSource<(bool, bool, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        CopyHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(RequeueMessage message, Envelope envelope)
    {
        if (envelope is not RabbitMqEnvelope rabbit) return;

        if (Interlocked.Increment(ref _deliveries) > 1)
        {
            CopyHandled.TrySetResult();
            return;
        }

        // Take away what the copy is published from, so publishing it fails the way a dead channel would
        var data = rabbit.Data;
        var original = rabbit.Message;
        if (_breakTheCopy)
        {
            rabbit.Data = null;
            rabbit.Message = null;
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

        rabbit.Data = data;
        rabbit.Message = original;

        var deadLettered = await CountAsync(_queueName + "_dlq");

        Requeued.TrySetResult((threw, rabbit.Acknowledged, deadLettered));
    }

    /// <summary>Messages in a queue, after a moment for the broker to route anything dead lettered</summary>
    public static async Task<uint> CountAsync(string queueName)
    {
        await Task.Delay(500);

        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queueName)).MessageCount;
    }
}
