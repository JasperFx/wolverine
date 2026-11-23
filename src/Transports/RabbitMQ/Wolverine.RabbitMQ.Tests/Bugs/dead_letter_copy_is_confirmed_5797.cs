using JasperFx.Core;
using System.Text;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5797. Interop-friendly (and enhanced) dead lettering sends a copy of the failed message to the dead letter queue
/// and acks the original. The copy went out on the dead letter queue's ordinary sender with no publisher confirm, and
/// through a retry block that returned normally even when the send failed, so the original was acked whether or not
/// the copy ever reached the broker. Now the copy is confirmed (and mandatory) first. A copy that cannot be sent hands
/// the original back to the broker, which delivers it again.
/// </summary>
public class dead_letter_copy_is_confirmed_5797
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<IHost> hostWith(string queueName, Action<RabbitMqQueue>? configureDeadLetterQueue = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.OnException<DeadLetterCopyFailure>().MoveToErrorQueue();

                opts.PublishMessage<DeadLetterCopyMessage>().ToRabbitQueue(queueName).SendInline();
                opts.ListenToRabbitQueue(queueName)
                    .DeadLetterQueueing(new DeadLetterQueue(queueName + "_dlq", DeadLetterQueueMode.InteropFriendly))
                    .PreFetchCount(1)
                    .ProcessInline();

                configureDeadLetterQueue?.Invoke(opts.RabbitMqTransport().Queues[queueName + "_dlq"]);
            }).StartAsync(Token);
    }

    [Fact]
    public async Task a_dead_letter_copy_the_broker_refuses_leaves_the_original_with_the_broker()
    {
        var queueName = RabbitTesting.NextQueueName();
        DeadLetterCopyHandler.Reset(queueName + "_dlq", DeadLetterSabotage.FillTheDeadLetterQueue);

        try
        {
            // One ready message fills the dead letter queue, and a full queue refuses a publish
            using var host = await hostWith(queueName, dlq =>
            {
                dlq.Arguments["x-max-length"] = 1;
                dlq.Arguments["x-overflow"] = "reject-publish";
            });

            await host.MessageBus().PublishAsync(new DeadLetterCopyMessage(false));

            // The original was not acked, so the broker delivers it again
            await DeadLetterCopyHandler.Redelivered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            (await DeadLetterCopyHandler.CountAsync(queueName + "_dlq")).ShouldBe(1u, "only the filler is in the dead letter queue");

            await host.StopAsync(Token);
        }
        finally
        {
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName);
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName + "_dlq");
        }
    }

    [Fact]
    public async Task a_dead_letter_copy_the_broker_cannot_route_leaves_the_original_with_the_broker()
    {
        var queueName = RabbitTesting.NextQueueName();
        DeadLetterCopyHandler.Reset(queueName + "_dlq", DeadLetterSabotage.DeleteTheDeadLetterQueue);

        try
        {
            using var host = await hostWith(queueName);

            await host.MessageBus().PublishAsync(new DeadLetterCopyMessage(false));

            // Without mandatory the broker confirms an unroutable copy and drops it, and the original was acked
            await DeadLetterCopyHandler.Redelivered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            await host.StopAsync(Token);
        }
        finally
        {
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName);
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName + "_dlq");
        }
    }

    [Fact]
    public async Task a_dead_letter_copy_the_broker_accepts_is_dead_lettered_once_and_the_original_settled()
    {
        var queueName = RabbitTesting.NextQueueName();
        DeadLetterCopyHandler.Reset(queueName + "_dlq", DeadLetterSabotage.None);

        try
        {
            using var host = await hostWith(queueName);

            await host.MessageBus().PublishAsync(new DeadLetterCopyMessage(false));

            await DeadLetterCopyHandler.Failed.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            var deadLettered = 0u;
            for (var i = 0; i < 20 && deadLettered == 0; i++)
            {
                deadLettered = await DeadLetterCopyHandler.CountAsync(queueName + "_dlq");
            }

            deadLettered.ShouldBe(1u);

            // Settled: neither redelivered nor left on the queue
            await Task.Delay(1.Seconds(), Token);
            DeadLetterCopyHandler.Redelivered.Task.IsCompleted.ShouldBeFalse();

            await host.StopAsync(Token);

            (await DeadLetterCopyHandler.CountAsync(queueName)).ShouldBe(0u);
        }
        finally
        {
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName);
            await DeadLetterCopyHandler.DeleteQueueAsync(queueName + "_dlq");
        }
    }
}

public record DeadLetterCopyMessage(bool Filler);

public class DeadLetterCopyFailure : Exception
{
    public DeadLetterCopyFailure() : base("Fails the first delivery so it is dead lettered")
    {
    }
}

public enum DeadLetterSabotage
{
    None,
    FillTheDeadLetterQueue,
    DeleteTheDeadLetterQueue
}

public static class DeadLetterCopyHandler
{
    private static int _deliveries;
    private static DeadLetterSabotage _sabotage;
    private static string _deadLetterQueueName = "";

    public static TaskCompletionSource Failed { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource Redelivered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset(string deadLetterQueueName, DeadLetterSabotage sabotage)
    {
        _deadLetterQueueName = deadLetterQueueName;
        _sabotage = sabotage;
        _deliveries = 0;
        Failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Redelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(DeadLetterCopyMessage message)
    {
        if (message.Filler) return;

        if (Interlocked.Increment(ref _deliveries) > 1)
        {
            Redelivered.TrySetResult();
            return;
        }

        switch (_sabotage)
        {
            case DeadLetterSabotage.FillTheDeadLetterQueue:
                await fillAsync(_deadLetterQueueName);
                break;
            case DeadLetterSabotage.DeleteTheDeadLetterQueue:
                await DeleteQueueAsync(_deadLetterQueueName);
                break;
        }

        Failed.TrySetResult();
        throw new DeadLetterCopyFailure();
    }

    // Publishes one message straight to the queue and waits for the broker to confirm it is there
    private static async Task fillAsync(string queueName)
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));

        var props = new BasicProperties
        {
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>
            {
                [EnvelopeConstants.MessageTypeKey] = typeof(DeadLetterCopyMessage).ToMessageTypeName(),
                [EnvelopeConstants.IdKey] = Guid.NewGuid().ToString()
            }
        };

        await channel.BasicPublishAsync("", queueName, true, props, Encoding.UTF8.GetBytes("{\"Filler\":true}"));

        (await channel.QueueDeclarePassiveAsync(queueName)).MessageCount.ShouldBe(1u);
    }

    public static async Task<uint> CountAsync(string queueName)
    {
        await Task.Delay(250);

        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queueName)).MessageCount;
    }

    public static async Task DeleteQueueAsync(string queueName)
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queueName, false, false);
    }
}
