using System.Text;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public record Explore5044RmqCommand(Guid Id);

public record Explore5044RmqEvent(Guid Id);

public class Explore5044RmqCommandHandler
{
    // A Marten session makes AutoApplyTransactions wrap this handler, so the cascaded event is written to the
    // outbox inside the Marten transaction and sent inline once it commits (SendAllOutgoingMessagesInlineAsync)
    public static Explore5044RmqEvent Handle(Explore5044RmqCommand command, IDocumentSession session)
    {
        return new Explore5044RmqEvent(command.Id);
    }
}

/// <summary>
/// PR #5044 exploratory testing: DurabilityMode.ServerlessWithOutbox with RabbitMQ. Under that mode the outbox row
/// of a cascaded message is deleted as soon as the inline send "was accepted by the broker". RabbitMQ publishes
/// without publisher confirms by default and never with mandatory=true, so a returned BasicPublishAsync proves
/// nothing about the broker having the message.
/// </summary>
public class serverless_with_outbox_rabbitmq_exploratory_5044
{
    private const string SchemaName = "explore5044rmq";

    private readonly ITestOutputHelper _output;

    public serverless_with_outbox_rabbitmq_exploratory_5044(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string uniqueName(string kind) => $"explore5044rmq-{kind}-{Guid.NewGuid().ToString("N")[..8]}";

    private static async Task<IHost> startHostAsync(Action<WolverineOptions> routing, string clientName,
        bool publisherConfirms = false, bool resourceSetupOnStartup = false)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                // So a durability pass in the test can pick up rows right away
                opts.Durability.OutboxStaleTime = 1.Seconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044RmqCommandHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                var rabbit = opts.UseRabbitMq(f => f.ClientProvidedName = clientName).AutoProvision();
                if (publisherConfirms)
                {
                    rabbit.ConfigureChannelCreation(o =>
                    {
                        o.PublisherConfirmationsEnabled = true;
                        o.PublisherConfirmationTrackingEnabled = true;
                    });
                }

                routing(opts);

                if (resourceSetupOnStartup) opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await host.GetRuntime().Storage.Admin.ClearAllAsync();

        return host;
    }

    private static async Task<IConnection> rawConnectionAsync()
    {
        var factory = new ConnectionFactory { HostName = "localhost", ClientProvidedName = uniqueName("raw") };
        return await factory.CreateConnectionAsync(Token);
    }

    private static async Task<uint> messageCountAsync(IConnection connection, string queueName)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Token);
        var ok = await channel.QueueDeclarePassiveAsync(queueName, Token);
        return ok.MessageCount;
    }

    private static async Task<IReadOnlyList<Envelope>> outboxAsync(IHost host)
    {
        return await host.GetRuntime().Storage.Admin.AllOutgoingAsync();
    }

    private static async Task<bool> waitForMessageAsync(IConnection connection, string queueName, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await messageCountAsync(connection, queueName) > 0) return true;
            await Task.Delay(100.Milliseconds(), Token);
        }

        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_rabbit_sending_endpoint_is_durable(bool resourceSetupOnStartup)
    {
        var queueName = uniqueName("mode");
        using var host = await startHostAsync(opts => opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName),
            uniqueName("client"), resourceSetupOnStartup: resourceSetupOnStartup);

        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri($"rabbitmq://queue/{queueName}"));
        _output.WriteLine($"resource setup on startup: {resourceSetupOnStartup}, endpoint mode: {agent.Endpoint.Mode}, agent: {agent.GetType().Name}, durable: {agent.IsDurable}");

        agent.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
        agent.IsDurable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task plain_serverless_downgrades_a_durable_rabbit_endpoint_to_inline(bool resourceSetupOnStartup)
    {
        // The same ordering question for the pre-existing Serverless policy
        var queueName = uniqueName("serverless");
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);
                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName).UseDurableOutbox();
                if (resourceSetupOnStartup) opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri($"rabbitmq://queue/{queueName}"));
        _output.WriteLine($"Serverless, resource setup on startup: {resourceSetupOnStartup}, endpoint mode: {agent.Endpoint.Mode}, agent: {agent.GetType().Name}");

        agent.Endpoint.Mode.ShouldBe(EndpointMode.Inline);
    }

    [Fact]
    public async Task happy_path_cascaded_message_lands_in_the_queue_and_the_outbox_is_empty()
    {
        var queueName = uniqueName("happy");
        using var host = await startHostAsync(opts => opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName),
            uniqueName("client"));

        await using var raw = await rawConnectionAsync();

        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);

        (await waitForMessageAsync(raw, queueName, 5.Seconds())).ShouldBeTrue("the cascaded message never reached the queue");
        (await outboxAsync(host)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task envelope_reply_uri_points_at_a_response_queue_nobody_will_ever_listen_to(bool disableReplyQueue)
    {
        var queueName = uniqueName("reply");
        using var host = await startHostAsync(opts =>
            {
                opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName);
                if (disableReplyQueue) opts.UseRabbitMq().DisableSystemRequestReplyQueueDeclaration();
            },
            uniqueName("client"));

        await using var raw = await rawConnectionAsync();
        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);
        (await waitForMessageAsync(raw, queueName, 5.Seconds())).ShouldBeTrue();

        await using var channel = await raw.CreateChannelAsync(cancellationToken: Token);
        var result = await channel.BasicGetAsync(queueName, true, Token);
        result.ShouldNotBeNull();

        var headers = result.BasicProperties.Headers!;
        var replyUri = headers.TryGetValue(EnvelopeConstants.ReplyUriKey, out var raw1) && raw1 is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;
        _output.WriteLine($"reply-uri header: {replyUri ?? "(none)"}");

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<Wolverine.RabbitMQ.Internal.RabbitMqTransport>();
        var responseQueues = transport.Queues.Where(x => x.QueueName.StartsWith("wolverine.response.")).ToArray();
        foreach (var queue in responseQueues)
        {
            _output.WriteLine($"response queue {queue.QueueName}: broker message count {await messageCountAsync(raw, queue.QueueName)} (declared on the broker at startup)");
        }

        // No listener runs under ServerlessWithOutbox, so a reply-uri that names this process's response queue
        // sends any reply (or a RespondToSender cascade) to a queue that no one will ever consume
        replyUri.ShouldBeNull("ServerlessWithOutbox stamps a reply-uri for a response queue that is never listened to");
    }

    [Fact]
    public async Task queue_deleted_after_startup_message_is_dropped_but_outbox_row_is_deleted()
    {
        // Default exchange + a routing key for a queue that does not exist: RabbitMQ silently drops an unroutable
        // message published with mandatory=false, publisher confirms on or off
        var queueName = uniqueName("deleted-queue");
        using var host = await startHostAsync(opts => opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName),
            uniqueName("client"));

        await using var raw = await rawConnectionAsync();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.QueueDeleteAsync(queueName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);

        var outbox = await outboxAsync(host);
        _output.WriteLine($"outbox rows after the inline send: {outbox.Count}");

        var report = await host.RunDurabilityPassAsync(10.Seconds(), Token);
        _output.WriteLine($"durability pass sent {report.OutgoingSent}");

        outbox.Count.ShouldBe(1, "the message was never routed to any queue, yet its outbox row was deleted");
    }

    [Fact]
    public async Task exchange_deleted_after_startup_message_is_lost_but_outbox_row_is_deleted()
    {
        var exchangeName = uniqueName("exchange");
        var queueName = uniqueName("bound");
        using var host = await startHostAsync(opts =>
                opts.PublishMessage<Explore5044RmqEvent>().ToRabbitExchange(exchangeName, e => e.BindQueue(queueName)),
            uniqueName("client"));

        await using var raw = await rawConnectionAsync();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeleteAsync(exchangeName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);

        var outbox = await outboxAsync(host);
        _output.WriteLine($"outbox rows after the inline send: {outbox.Count}");
        (await messageCountAsync(raw, queueName)).ShouldBe(0u);

        // Not a lost row if the pass can still recover it once the exchange is back
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: Token);
            await channel.QueueBindAsync(queueName, exchangeName, "", cancellationToken: Token);
        }

        await Task.Delay(1500.Milliseconds(), Token);
        var report = await host.RunDurabilityPassAsync(10.Seconds(), Token);
        _output.WriteLine($"durability pass sent {report.OutgoingSent}");

        outbox.Count.ShouldBe(1, "a publish to an exchange that does not exist closed the channel with a 404, yet the outbox row was deleted");
        (await waitForMessageAsync(raw, queueName, 5.Seconds())).ShouldBeTrue("the message was never recovered");
    }

    [Fact]
    public async Task exchange_deleted_no_transaction_publish_also_loses_the_message()
    {
        // DurableSendingAgent.storeAndForwardAsync path: no outbox transaction, store, send inline, delete
        var exchangeName = uniqueName("exchange-nt");
        var queueName = uniqueName("bound-nt");
        using var host = await startHostAsync(opts =>
                opts.PublishMessage<Explore5044RmqEvent>().ToRabbitExchange(exchangeName, e => e.BindQueue(queueName)),
            uniqueName("client"));

        await using var raw = await rawConnectionAsync();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeleteAsync(exchangeName, cancellationToken: Token);
        }

        await host.MessageBus().PublishAsync(new Explore5044RmqEvent(Guid.NewGuid()));

        var outbox = await outboxAsync(host);
        _output.WriteLine($"outbox rows after the inline send: {outbox.Count}");

        outbox.Count.ShouldBe(1, "a publish to an exchange that does not exist closed the channel with a 404, yet the outbox row was deleted");
    }

    [Fact]
    public async Task with_publisher_confirms_a_publish_to_a_deleted_exchange_keeps_the_outbox_row()
    {
        var exchangeName = uniqueName("exchange-confirm");
        var queueName = uniqueName("bound-confirm");
        using var host = await startHostAsync(opts =>
                opts.PublishMessage<Explore5044RmqEvent>().ToRabbitExchange(exchangeName, e => e.BindQueue(queueName)),
            uniqueName("client"), publisherConfirms: true);

        await using var raw = await rawConnectionAsync();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeleteAsync(exchangeName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);

        var outbox = await outboxAsync(host);
        _output.WriteLine($"outbox rows after the inline send: {outbox.Count}");
        outbox.Count.ShouldBe(1);

        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: Token);
            await channel.QueueBindAsync(queueName, exchangeName, "", cancellationToken: Token);
        }

        await Task.Delay(1500.Milliseconds(), Token);
        var report = await host.RunDurabilityPassAsync(10.Seconds(), Token);
        _output.WriteLine($"durability pass sent {report.OutgoingSent}");

        (await waitForMessageAsync(raw, queueName, 5.Seconds())).ShouldBeTrue("the durability pass never recovered the message");
        (await outboxAsync(host)).ShouldBeEmpty();
    }

    [Fact]
    public async Task raw_client_mandatory_publish_with_confirm_tracking_throws_for_an_unroutable_message()
    {
        // What a fix for the unroutable case would rely on: RabbitMQ.Client 7 turns a basic.return into an
        // exception when the channel tracks publisher confirms and the publish is mandatory
        await using var raw = await rawConnectionAsync();
        await using var channel = await raw.CreateChannelAsync(new CreateChannelOptions(true, true), Token);

        var props = new BasicProperties();
        var ex = await Should.ThrowAsync<Exception>(async () =>
            await channel.BasicPublishAsync("", uniqueName("nowhere"), true, props, new byte[] { 1 }, Token));
        _output.WriteLine($"{ex.GetType().FullName}: {ex.Message}");

        // And without mandatory the same publish is confirmed although it went nowhere
        await channel.BasicPublishAsync("", uniqueName("nowhere"), false, props, new byte[] { 1 }, Token);
    }

    [Fact]
    public async Task with_publisher_confirms_an_unroutable_message_is_still_dropped_and_deleted()
    {
        var queueName = uniqueName("deleted-queue-confirm");
        using var host = await startHostAsync(opts => opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName),
            uniqueName("client"), publisherConfirms: true);

        await using var raw = await rawConnectionAsync();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.QueueDeleteAsync(queueName, cancellationToken: Token);
        }

        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);

        var outbox = await outboxAsync(host);
        _output.WriteLine($"outbox rows after the inline send: {outbox.Count}");

        // The broker acks an unroutable message that was published with mandatory=false
        outbox.Count.ShouldBe(1, "publisher confirms do not catch an unroutable message published with mandatory=false");
    }

    [Fact]
    public async Task connection_killed_by_the_broker_just_before_the_send()
    {
        var queueName = uniqueName("killed");
        var clientName = uniqueName("client");
        using var host = await startHostAsync(opts => opts.PublishMessage<Explore5044RmqEvent>().ToRabbitQueue(queueName),
            clientName);

        await using var raw = await rawConnectionAsync();

        // Warm the sender so it has a live channel to lose
        await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);
        (await waitForMessageAsync(raw, queueName, 5.Seconds())).ShouldBeTrue();
        await using (var channel = await raw.CreateChannelAsync(cancellationToken: Token))
        {
            await channel.QueuePurgeAsync(queueName, Token);
        }

        using var probe = await RabbitManagementProbe.RequireAsync(Token);
        (await probe.WaitForConnectionAsync(clientName, 30.Seconds(), Token)).ShouldBeTrue();
        var killed = await probe.ForceCloseConnectionsAsync(clientName, Token);
        _output.WriteLine($"killed {killed} connection(s)");
        killed.ShouldBeGreaterThan(0);

        await Task.Delay(200.Milliseconds(), Token);

        Exception? thrown = null;
        try
        {
            await host.MessageBus().InvokeAsync(new Explore5044RmqCommand(Guid.NewGuid()), Token);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        _output.WriteLine($"invoke threw: {thrown?.GetType().Name ?? "nothing"} {thrown?.Message}");

        var outbox = await outboxAsync(host);
        var arrived = await waitForMessageAsync(raw, queueName, 3.Seconds());
        _output.WriteLine($"outbox rows: {outbox.Count}, arrived: {arrived}");

        (arrived || outbox.Count == 1).ShouldBeTrue("the message neither arrived nor stayed in the outbox");

        // Once the connection auto-recovers, a durability pass has to get the message out
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (!arrived && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(1.Seconds(), Token);
            var report = await host.RunDurabilityPassAsync(10.Seconds(), Token);
            _output.WriteLine($"durability pass sent {report.OutgoingSent}, failed {report.OutgoingFailed}");
            arrived = await waitForMessageAsync(raw, queueName, 1.Seconds());
        }

        arrived.ShouldBeTrue("the durability pass never recovered the message after the connection came back");
        (await outboxAsync(host)).ShouldBeEmpty();
    }
}
