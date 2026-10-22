using System.Text;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5123. What the listener's transient give-back costs on a RabbitMQ 4 quorum queue. While it is latched or
/// disconnected, WorkerQueueMessageConsumer hands deliveries back with basic.reject(requeue: true). On a quorum
/// queue every requeued give-back counts against the delivery limit (20 by default since RabbitMQ 4), so a listener
/// that keeps giving the same message back -- through a broker outage and its reconnects -- runs it out of
/// deliveries, and with no dead letter exchange the broker drops it.
///
/// <para>
/// The report suggested basic.nack instead, on the reading that a nack does not count. That depends on the broker:
/// RabbitMQ 4.2.5 counted a nack exactly like a reject (both dropped on the 21st give-back), while the newer 4.x
/// broker CI pulls for the floating rabbitmq:4 tag gave a nacked message back 60 times without dropping it. A
/// nack is never worse, so the listener now uses one. These facts pin that relationship rather than one version.
/// </para>
/// </summary>
public class Bug_5123_quorum_queue_delivery_limit
{
    private const int DefaultDeliveryLimit = 20;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task rejecting_with_requeue_spends_the_delivery_limit()
    {
        (await giveBacksUntilDroppedAsync((channel, tag) => channel.BasicRejectAsync(tag, true, Token)))
            .ShouldBe(DefaultDeliveryLimit + 1);
    }

    [Fact]
    public async Task nacking_with_requeue_never_spends_more_of_the_delivery_limit_than_rejecting()
    {
        var nacks = await giveBacksUntilDroppedAsync((channel, tag) => channel.BasicNackAsync(tag, false, true, Token));

        // Dropped on the 21st on RabbitMQ 4.2.5; never dropped (the loop's 60 cap) on newer 4.x brokers
        nacks.ShouldBeGreaterThanOrEqualTo(DefaultDeliveryLimit + 1);
    }

    /// <summary>Gives one message on a fresh quorum queue back until the broker stops delivering it.</summary>
    private static async Task<int> giveBacksUntilDroppedAsync(Func<IChannel, ulong, ValueTask> giveBack)
    {
        var queue = $"bug-5123-{Guid.NewGuid():N}";

        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync(Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Token);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: Token);

        try
        {
            await channel.BasicPublishAsync("", queue, Encoding.UTF8.GetBytes("payload"), Token);

            var giveBacks = 0;
            while (giveBacks < DefaultDeliveryLimit * 3)
            {
                var delivery = await getAsync(channel, queue);
                if (delivery == null) break;

                await giveBack(channel, delivery.DeliveryTag);
                giveBacks++;
            }

            return giveBacks;
        }
        finally
        {
            await channel.QueueDeleteAsync(queue, cancellationToken: Token);
        }
    }

    // A requeued message can take a moment to become available again
    private static async Task<BasicGetResult?> getAsync(IChannel channel, string queue)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false, Token);
            if (result != null) return result;
            await Task.Delay(20, Token);
        }

        return null;
    }
}
