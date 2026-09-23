using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4520. A mismatched queue declaration used to log a warning, return as if the declaration had
/// succeeded, and leave the channel dead -- the broker answers a mismatch with a channel-level 406.
/// The failure then surfaced one hop later as "Unable to open a Rabbit MQ channel for listener ...
/// The underlying failure was logged by the channel agent", which is both the wrong cause and a
/// pointer at the log rather than an answer.
/// </summary>
public class inequivalent_queue_declaration_4520
{
    [Fact]
    public void the_message_quotes_the_brokers_own_complaint_and_all_three_remedies()
    {
        var transport = new RabbitMqTransport();
        var queue = transport.Queues["orders"];
        queue.QueueType = QueueType.quorum;
        queue.Arguments["x-dead-letter-exchange"] = "wolverine-dead-letter-queue";

        // What the broker actually replies with on a 406 PRECONDITION_FAILED
        const string brokerReply =
            "inequivalent arg 'x-dead-letter-exchange' for queue 'orders' in vhost '/': received the value 'wolverine-dead-letter-queue' of type 'longstr' but current is none";

        var message = RabbitMqQueue.InequivalentArgumentMessage("orders", queue, brokerReply);

        // names the queue
        message.ShouldContain("orders");

        // quotes what the broker actually objected to
        message.ShouldContain("inequivalent arg 'x-dead-letter-exchange'");

        // names what Wolverine tried to declare
        message.ShouldContain("IsDurable=");
        message.ShouldContain("x-dead-letter-exchange=wolverine-dead-letter-queue");

        // and all three remedies
        message.ShouldContain("ListenToRabbitQueue(\"orders\"");
        message.ShouldContain("delete the existing queue");
        message.ShouldContain("ExternallyOwned()");
    }

    [Fact]
    public async Task a_mismatched_declaration_fails_at_declaration_time_against_a_real_broker()
    {
        var queueName = RabbitTesting.NextQueueName();

        // Stand up the queue out of band with one dead-letter-exchange argument, the way an older
        // version of the app -- or another team, or the management UI -- would have left it.
        var factory = new ConnectionFactory { HostName = "localhost" };
        await using (var connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken))
        await using (var channel = await connection.CreateChannelAsync(
                         cancellationToken: TestContext.Current.CancellationToken))
        {
            await channel.QueueDeclareAsync(queueName, true, false, false,
                new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "someone-elses-dlx" },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        try
        {
            // Now ask Wolverine to declare the same queue with a different dead letter exchange.
            var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                using var host = await Host.CreateDefaultBuilder()
                    .UseWolverine(opts =>
                    {
                        opts.UseRabbitMq().AutoProvision();
                        opts.ListenToRabbitQueue(queueName);
                    }).StartAsync(TestContext.Current.CancellationToken);
            });

            ex.Message.ShouldContain(queueName);
            ex.Message.ShouldContain("inequivalent arg");
            ex.Message.ShouldContain("ExternallyOwned()");

            // the broker's original 406 is still there to drill into
            ex.InnerException.ShouldBeOfType<OperationInterruptedException>();
        }
        finally
        {
            await using var connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
            await using var channel = await connection.CreateChannelAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            await channel.QueueDeleteAsync(queueName, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task a_matching_declaration_still_succeeds()
    {
        var queueName = RabbitTesting.NextQueueName();

        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.HostName = "localhost");

        var queue = transport.Queues[queueName];

        await using var connection = await transport.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        await queue.DeclareAsync(channel, NullLogger.Instance);
        queue.HasDeclared.ShouldBeTrue();

        // declaring the very same shape a second time is not a mismatch
        await queue.DeclareAsync(channel, NullLogger.Instance);

        await channel.QueueDeleteAsync(queueName, cancellationToken: TestContext.Current.CancellationToken);
    }
}
