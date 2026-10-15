using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5016. Under ServerlessWithOutbox an outbox row is deleted as soon as its inline publish returns. Without
/// publisher confirms, BasicPublishAsync returns before the broker has decided anything, so a publish the broker
/// rejected (an exchange that is gone) or dropped (a queue that is gone) still had its row deleted, and the message
/// was lost. The mode now forces publisher confirms with tracking on every channel, and publishes to a queue as
/// mandatory, with nothing configured by the application.
/// </summary>
public class serverless_with_outbox_publisher_confirms_5016
{
    private const string SchemaName = "serverless_rmq_confirms";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string uniqueName(string kind) => $"serverless-confirms-{kind}-{Guid.NewGuid().ToString("N")[..8]}";

    private static async Task<IHost> startHostAsync(Action<WolverineOptions> routing)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                // So a durability pass in the test picks up a row right away
                opts.Durability.OutboxStaleTime = 1.Seconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ConfirmsCommandHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                // Deliberately no ConfigureChannelCreation(): the mode has to turn confirms on by itself
                opts.UseRabbitMq().AutoProvision();

                routing(opts);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await host.GetRuntime().Storage.Admin.ClearAllAsync();

        return host;
    }

    private static async Task<IConnection> rawConnectionAsync()
    {
        return await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync(Token);
    }

    private static async Task<uint> messageCountAsync(IConnection connection, string queueName)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Token);
        return (await channel.QueueDeclarePassiveAsync(queueName, Token)).MessageCount;
    }

    private static async Task shouldReceiveAsync(IConnection connection, string queueName)
    {
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (await messageCountAsync(connection, queueName) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Nothing ever reached {queueName}");
            await Task.Delay(100.Milliseconds(), Token);
        }
    }

    private static async Task<int> outboxCountAsync(IHost host)
    {
        return (await host.GetRuntime().Storage.Admin.AllOutgoingAsync()).Count;
    }

    [Fact]
    public async Task a_cascaded_message_is_confirmed_and_its_row_deleted()
    {
        var queueName = uniqueName("happy");
        using var host = await startHostAsync(opts => opts.PublishMessage<ConfirmsEvent>().ToRabbitQueue(queueName));
        await using var raw = await rawConnectionAsync();

        await host.MessageBus().InvokeAsync(new ConfirmsCommand(Guid.NewGuid()), Token);

        (await outboxCountAsync(host)).ShouldBe(0);
        (await messageCountAsync(raw, queueName)).ShouldBe(1u);
    }

    [Fact]
    public async Task a_publish_to_an_exchange_that_is_gone_keeps_its_row_for_a_pass()
    {
        var exchangeName = uniqueName("exchange");
        var queueName = uniqueName("bound");
        using var host = await startHostAsync(opts =>
            opts.PublishMessage<ConfirmsEvent>().ToRabbitExchange(exchangeName, e => e.BindQueue(queueName)));
        await using var raw = await rawConnectionAsync();

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeleteAsync(exchangeName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new ConfirmsCommand(Guid.NewGuid()), Token);

        // The broker refused it with a 404, which a confirmed publish surfaces
        (await outboxCountAsync(host)).ShouldBe(1);

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false,
                cancellationToken: Token);
            await channel.QueueBindAsync(queueName, exchangeName, "", cancellationToken: Token);
        }

        await Task.Delay(1500.Milliseconds(), Token);
        await host.RunDurabilityPassAsync(10.Seconds(), Token);

        await shouldReceiveAsync(raw, queueName);
        (await outboxCountAsync(host)).ShouldBe(0);
    }

    [Fact]
    public async Task a_publish_with_no_outbox_transaction_to_an_exchange_that_is_gone_keeps_its_row()
    {
        // The DurableSendingAgent path: store, send inline, delete only once confirmed
        var exchangeName = uniqueName("exchange-nt");
        var queueName = uniqueName("bound-nt");
        using var host = await startHostAsync(opts =>
            opts.PublishMessage<ConfirmsEvent>().ToRabbitExchange(exchangeName, e => e.BindQueue(queueName)));
        await using var raw = await rawConnectionAsync();

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeleteAsync(exchangeName, cancellationToken: Token);
        }

        await host.MessageBus().PublishAsync(new ConfirmsEvent(Guid.NewGuid()));

        (await outboxCountAsync(host)).ShouldBe(1);
    }

    [Fact]
    public async Task a_publish_to_a_queue_that_is_gone_keeps_its_row_for_a_pass()
    {
        var queueName = uniqueName("deleted-queue");
        using var host = await startHostAsync(opts => opts.PublishMessage<ConfirmsEvent>().ToRabbitQueue(queueName));
        await using var raw = await rawConnectionAsync();

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.QueueDeleteAsync(queueName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new ConfirmsCommand(Guid.NewGuid()), Token);

        // A mandatory publish to a queue that does not exist comes back unroutable rather than being dropped
        (await outboxCountAsync(host)).ShouldBe(1);

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: Token);
        }

        await Task.Delay(1500.Milliseconds(), Token);
        await host.RunDurabilityPassAsync(10.Seconds(), Token);

        await shouldReceiveAsync(raw, queueName);
        (await outboxCountAsync(host)).ShouldBe(0);
    }
}

public record ConfirmsCommand(Guid Id);

public record ConfirmsEvent(Guid Id);

public class ConfirmsCommandHandler
{
    // The Marten session makes AutoApplyTransactions wrap this handler, so the cascaded event is written to the
    // outbox in the Marten transaction and sent inline once it commits
    public static ConfirmsEvent Handle(ConfirmsCommand command, IDocumentSession session)
    {
        return new ConfirmsEvent(command.Id);
    }
}
