using System.Collections.Concurrent;
using System.Text;
using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Marten;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5123. A durable listener settles a message the moment it is in the inbox, so on a RabbitMQ 4 quorum queue the
/// broker's delivery limit (20 by default) only ever counts the times Wolverine handed a delivery back -- a restart,
/// a reconnect, a shutdown with deliveries still prefetched. Enough of those during an outage and the broker drops
/// the message (see <see cref="Bug_5123_quorum_queue_delivery_limit" /> for the broker's semantics). These facts
/// cover the opt-in delivery limit, the startup warning when there is none, and the order of a deferral.
/// </summary>
public class Bug_5123_quorum_queues_behind_a_durable_inbox
{
    private const int DefaultDeliveryLimit = 20;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IHostBuilder durableQuorumListener(string queueName, CapturedWarnings warnings,
        Action<RabbitMqTransportExpression>? transport = null, Action<RabbitMqListenerConfiguration>? listener = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(warnings))
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh5123";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "gh5123");

                var rabbit = opts.UseRabbitMq().AutoProvision();
                transport?.Invoke(rabbit);

                var configuration = opts.ListenToRabbitQueue(queueName).QueueType(QueueType.quorum).UseDurableInbox();
                listener?.Invoke(configuration);

                opts.Services.AddResourceSetupOnStartup();
            });
    }

    [Fact]
    public async Task a_durable_quorum_queue_is_declared_exactly_as_before_unless_you_opt_in()
    {
        var queueName = RabbitTesting.NextQueueName();
        var warnings = new CapturedWarnings();

        using var host = await durableQuorumListener(queueName, warnings).StartAsync(Token);

        try
        {
            // RabbitMQ refuses to redeclare an existing queue with different arguments, so the default must not move
            host.GetRuntime().Options.RabbitMqTransport().Queues[queueName].Arguments
                .ShouldNotContainKey(RabbitMqTransport.DeliveryLimitHeader);

            await host.StopAsync(Token);

            (await giveBacksUntilDroppedAsync(queueName)).ShouldBe(DefaultDeliveryLimit + 1);
        }
        finally
        {
            await deleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task warns_at_startup_when_a_durable_quorum_queue_has_no_delivery_limit()
    {
        var queueName = RabbitTesting.NextQueueName();
        var warnings = new CapturedWarnings();

        using var host = await durableQuorumListener(queueName, warnings).StartAsync(Token);

        try
        {
            warnings.Messages.Count(x => x.Contains(queueName) && x.Contains("x-delivery-limit")).ShouldBe(1);
            await host.StopAsync(Token);
        }
        finally
        {
            await deleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task a_listener_can_opt_out_of_the_delivery_limit()
    {
        var queueName = RabbitTesting.NextQueueName();
        var warnings = new CapturedWarnings();

        using var host = await durableQuorumListener(queueName, warnings, listener: x => x.DeliveryLimit(-1))
            .StartAsync(Token);

        try
        {
            warnings.Messages.ShouldNotContain(x => x.Contains(queueName) && x.Contains("x-delivery-limit"));
            await host.StopAsync(Token);

            // Never dropped: the helper gives up at three times the default limit
            (await giveBacksUntilDroppedAsync(queueName)).ShouldBe(DefaultDeliveryLimit * 3);
        }
        finally
        {
            await deleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task the_transport_can_set_the_delivery_limit_for_every_durable_quorum_queue()
    {
        var queueName = RabbitTesting.NextQueueName();
        var warnings = new CapturedWarnings();

        using var host = await durableQuorumListener(queueName, warnings,
                transport: x => x.DeliveryLimitForDurableQuorumQueues(30))
            .StartAsync(Token);

        try
        {
            warnings.Messages.ShouldNotContain(x => x.Contains(queueName) && x.Contains("x-delivery-limit"));
            await host.StopAsync(Token);

            (await giveBacksUntilDroppedAsync(queueName)).ShouldBe(31);
        }
        finally
        {
            await deleteQueueAsync(queueName);
        }
    }

    [Fact]
    public async Task the_transport_wide_delivery_limit_leaves_queues_that_are_not_durable_listeners_alone()
    {
        var queueName = RabbitTesting.NextQueueName();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;
                opts.UseRabbitMq().AutoProvision().DeliveryLimitForDurableQuorumQueues(-1);
                opts.ListenToRabbitQueue(queueName).QueueType(QueueType.quorum).ProcessInline();
            }).StartAsync(Token);

        try
        {
            host.GetRuntime().Options.RabbitMqTransport().Queues[queueName].Arguments
                .ShouldNotContainKey(RabbitMqTransport.DeliveryLimitHeader);

            await host.StopAsync(Token);
        }
        finally
        {
            await deleteQueueAsync(queueName);
        }
    }

    /// <summary>
    /// GH-5123 (3). Deferring acked the original delivery and THEN published the copy, so a copy that could not be
    /// published -- a channel or connection that was not usable -- left the message nowhere. The ack still comes
    /// after the copy rather than leaving the original unsettled, which would hold a prefetch slot.
    /// </summary>
    [Fact]
    public async Task deferring_does_not_ack_the_original_when_the_copy_cannot_be_published()
    {
        var queueName = RabbitTesting.NextQueueName();
        Bug5123DeferHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<Bug5123DeferMessage>().ToRabbitQueue(queueName).SendInline();
                opts.ListenToRabbitQueue(queueName).ProcessInline();
            }).StartAsync(Token);

        await host.MessageBus().PublishAsync(new Bug5123DeferMessage());

        var (threw, acknowledged) = await Bug5123DeferHandler.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        threw.ShouldBeTrue("the copy cannot be published, so the deferral has to fail");
        acknowledged.ShouldBeFalse("the original is the only copy of the message left");

        await host.StopAsync(Token);
    }

    /// <summary>Gives one message on an existing queue back until the broker stops delivering it.</summary>
    private static async Task<int> giveBacksUntilDroppedAsync(string queueName)
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync(Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Token);

        await channel.QueuePurgeAsync(queueName, Token);
        await channel.BasicPublishAsync("", queueName, Encoding.UTF8.GetBytes("payload"), Token);

        var giveBacks = 0;
        while (giveBacks < DefaultDeliveryLimit * 3)
        {
            var delivery = await getAsync(channel, queueName);
            if (delivery == null) break;

            await channel.BasicRejectAsync(delivery.DeliveryTag, true, Token);
            giveBacks++;
        }

        return giveBacks;
    }

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

    private static async Task deleteQueueAsync(string queueName)
    {
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queueName);
    }
}

public record Bug5123DeferMessage;

public static class Bug5123DeferHandler
{
    public static TaskCompletionSource<(bool Threw, bool Acknowledged)> Result { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Result = new TaskCompletionSource<(bool Threw, bool Acknowledged)>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(Bug5123DeferMessage message, Envelope envelope)
    {
        if (envelope is not RabbitMqEnvelope rabbit) return;

        // Take away what the copy is published from, so publishing the copy fails the way a dead channel would
        var data = rabbit.Data;
        var original = rabbit.Message;
        rabbit.Data = null;
        rabbit.Message = null;

        var threw = false;
        try
        {
            await rabbit.DeferAsync();
        }
        catch (Exception)
        {
            threw = true;
        }

        var acknowledged = rabbit.Acknowledged;

        rabbit.Data = data;
        rabbit.Message = original;

        Result.TrySetResult((threw, acknowledged));
    }
}

internal class CapturedWarnings : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) _messages.Enqueue(formatter(state, exception));
    }

    public void Dispose()
    {
    }
}
