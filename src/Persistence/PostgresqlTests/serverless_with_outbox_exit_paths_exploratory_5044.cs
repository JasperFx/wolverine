using System.Collections.Concurrent;
using System.Net.Http.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
/// Exploratory tests for PR #5044 (DurabilityMode.ServerlessWithOutbox): every way a message can leave a
/// serverless Wolverine app beyond the paths the PR covered. The external sends go over TCP, which uses a real
/// DurableSendingAgent (the stub transport sends synchronously and would hide background sends). A dead port
/// makes the send fail; a Solo receiver host on the port makes it succeed.
///
/// The assertion pattern for an exit path that must not lose a message: right after the call returns, either the
/// outbox is empty and the message was received, or the row is owned by any node (0) and a later durability pass
/// delivers it.
/// </summary>
public class serverless_with_outbox_exit_paths_exploratory_5044 : IAsyncDisposable
{
    private const string SchemaName = "explore5044_exit_pg";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
    private IHost? theFunction;
    private IHost? theReceiver;
    private WebApplication? theWebApp;

    public async ValueTask DisposeAsync()
    {
        if (theWebApp != null)
        {
            await theWebApp.StopAsync();
            await theWebApp.DisposeAsync();
        }

        if (theFunction != null)
        {
            await theFunction.StopAsync();
            theFunction.Dispose();
        }

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    private void configureFunction(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        opts.Durability.OutboxStaleTime = 100.Milliseconds();

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType(typeof(Explore5044FunctionHandlers))
            .IncludeType(typeof(Explore5044LocalOnlyHandler))
            .IncludeType(typeof(Explore5044Saga));

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

        opts.PublishMessage<Explore5044Remote>().ToPort(thePort);
        opts.PublishMessage<Explore5044RemoteRequest>().ToPort(thePort);

        opts.Services.AddResourceSetupOnStartup();
    }

    private async Task startTheFunctionAsync(Action<WolverineOptions>? configure = null)
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configureFunction(opts);
                configure?.Invoke(opts);
            }).StartAsync(Token);

        await theFunction.RebuildAllEnvelopeStorageAsync();
    }

    private async Task startTheReceiverAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044ReceiverHandler));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    private static async Task<List<(Guid Id, int OwnerId, string MessageType)>> rowsAsync(string table)
    {
        // Handled inbox rows are only kept for deduplication
        var where = table == DatabaseConstants.IncomingTable ? $" where status != '{EnvelopeStatus.Handled}'" : "";

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new List<(Guid, int, string)>();
        await using var reader = await conn
            .CreateCommand($"select id, owner_id, message_type from {SchemaName}.{table}{where}")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows.Add((await reader.GetFieldValueAsync<Guid>(0, Token), await reader.GetFieldValueAsync<int>(1, Token),
                await reader.GetFieldValueAsync<string>(2, Token)));
        }

        return rows;
    }

    private static Task<List<(Guid Id, int OwnerId, string MessageType)>> outboxRowsAsync()
        => rowsAsync(DatabaseConstants.OutgoingTable);

    private static Task<List<(Guid Id, int OwnerId, string MessageType)>> inboxRowsAsync()
        => rowsAsync(DatabaseConstants.IncomingTable);

    private static async Task shouldBeReceivedAsync(Guid id, int seconds = 10)
    {
        var timeout = DateTimeOffset.UtcNow.Add(seconds.Seconds());
        while (!Explore5044ReceiverHandler.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout)
            {
                throw new TimeoutException($"Message {id} never reached the receiver");
            }

            await Task.Delay(50, Token);
        }
    }

    // Either sent before the call returned (outbox empty + received), or owned by 0 and delivered by a pass
    private async Task shouldBeDeliveredNowOrByAPassAsync(Guid id)
    {
        var rows = await outboxRowsAsync();
        if (rows.Count == 0)
        {
            await shouldBeReceivedAsync(id);
            return;
        }

        rows.Count.ShouldBe(1);
        rows.Single().OwnerId.ShouldBe(TransportConstants.AnyNode);

        if (theReceiver == null) await startTheReceiverAsync();
        await Task.Delay(200, Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        await shouldBeReceivedAsync(id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------------------------------------------
    // 1. Scheduled sends to an external transport that cannot schedule natively (TCP)
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task schedule_async_to_tcp_is_refused_clearly()
    {
        await startTheFunctionAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await theFunction!.MessageBus().ScheduleAsync(new Explore5044Remote(Guid.NewGuid()), 1.Seconds()));

        ex.Message.ShouldContain("does not support native scheduling");
        (await outboxRowsAsync()).ShouldBeEmpty();
        (await inboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task publish_with_schedule_delay_to_tcp_is_refused_clearly()
    {
        await startTheFunctionAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await theFunction!.MessageBus().PublishAsync(new Explore5044Remote(Guid.NewGuid()),
                new DeliveryOptions { ScheduleDelay = 1.Seconds() }));

        ex.Message.ShouldContain("does not support native scheduling");
    }

    [Fact]
    public async Task endpoint_for_send_with_schedule_delay_to_tcp_is_refused_or_delivered_exactly_once()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();

        // DestinationEndpoint.SendAsync has its own copy of the "wrap for the local durable queue" logic
        // (DestinationEndpoint.cs:55-60) and does not go through MessageRoute.WriteEnvelope's GH-5016 guard.
        // MessageRouterBase only finds no local queue when the local transport is gone, but a DurableLocalQueue
        // sending agent for local://durable is still built and cached
        Exception? refused = null;
        try
        {
            await theFunction!.MessageBus().EndpointFor(new Uri($"tcp://localhost:{thePort}"))
                .SendAsync(new Explore5044Remote(id), new DeliveryOptions { ScheduleDelay = 1.Seconds() });
        }
        catch (InvalidOperationException e)
        {
            refused = e;
        }

        if (refused != null)
        {
            refused.Message.ShouldContain("does not support native scheduling");
            return;
        }

        var inbox = await inboxRowsAsync();
        Console.WriteLine(
            $"EndpointFor+ScheduleDelay inbox rows: {string.Join(", ", inbox.Select(x => $"{x.MessageType}/owner {x.OwnerId}"))}");

        // Keep the "function" alive past the due time without a pass: anything that fires now came from an
        // in-memory timer, which a frozen serverless function would never run
        await Task.Delay(2.Seconds(), Token);
        var firedInMemory = Explore5044ReceiverHandler.Received.TryGetValue(id, out var before) ? before : 0;

        await theFunction!.RunDurabilityPassAsync(token: Token);
        await Task.Delay(1.Seconds(), Token);

        var total = Explore5044ReceiverHandler.Received.TryGetValue(id, out var after) ? after : 0;
        Console.WriteLine(
            $"EndpointFor+ScheduleDelay: received {firedInMemory} time(s) before the pass, {total} in total");

        total.ShouldBe(1);
        (await inboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task scheduled_cascade_to_tcp_from_a_transactional_handler_is_refused_clearly()
    {
        await startTheFunctionAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await theFunction!.MessageBus().InvokeAsync(new Explore5044CascadeDelayed(Guid.NewGuid()), Token));

        ex.Message.ShouldContain("does not support native scheduling");
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------------------------------------------
    // 2. Messages handled by this same app (there are no local queues in this mode)
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task send_async_of_a_locally_handled_message_with_no_route()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();

        // SendAsync demands a route
        await Should.ThrowAsync<IndeterminateRoutesException>(async () =>
            await theFunction!.MessageBus().SendAsync(new Explore5044LocalOnly(id)));

        Explore5044LocalOnlyHandler.Handled.ContainsKey(id).ShouldBeFalse();
    }

    [Fact]
    public async Task publish_async_of_a_locally_handled_message_is_not_silently_dropped()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().PublishAsync(new Explore5044LocalOnly(id));

        await Task.Delay(500, Token);

        // The handler is in this very app. In every other mode conventional local routing runs it. Here it
        // is neither run, nor persisted for a pass, nor refused: PublishAsync quietly finds "no routes"
        var persisted = (await inboxRowsAsync()).Count + (await outboxRowsAsync()).Count;
        (Explore5044LocalOnlyHandler.Handled.ContainsKey(id) || persisted > 0).ShouldBeTrue(
            "PublishAsync of a message with a handler in this app was silently dropped");
    }

    [Fact]
    public async Task a_cascaded_message_with_a_local_handler_is_not_silently_dropped()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044CascadeLocal(id), Token);

        await Task.Delay(500, Token);

        var persisted = (await inboxRowsAsync()).Count + (await outboxRowsAsync()).Count;
        (Explore5044LocalOnlyHandler.Handled.ContainsKey(id) || persisted > 0).ShouldBeTrue(
            "A cascaded message handled by this same app was silently dropped");
    }

    [Fact]
    public async Task schedule_async_of_a_locally_handled_message_is_stored_and_released_by_a_pass()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().ScheduleAsync(new Explore5044LocalOnly(id), 500.Milliseconds());

        // The docs say "Scheduled messages: Released by the durability pass"
        var inbox = await inboxRowsAsync();
        inbox.Count.ShouldBe(1, "ScheduleAsync of a locally handled message persisted nothing");

        await Task.Delay(1.Seconds(), Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        Explore5044LocalOnlyHandler.Handled.ContainsKey(id).ShouldBeTrue();
    }

    [Fact]
    public async Task routing_explicitly_to_a_local_queue_fails_at_startup_or_send()
    {
        Exception? caught = null;
        try
        {
            await startTheFunctionAsync(opts => opts.PublishMessage<Explore5044LocalOnly>().ToLocalQueue("explore5044"));

            var id = Guid.NewGuid();
            await theFunction!.MessageBus().PublishAsync(new Explore5044LocalOnly(id));
            await Task.Delay(500, Token);

            Explore5044LocalOnlyHandler.Handled.ContainsKey(id).ShouldBeTrue(
                "An explicit ToLocalQueue() route was accepted at startup but the message was not handled");
        }
        catch (Exception e) when (e is not ShouldAssertException)
        {
            caught = e;
        }

        // Record what happened: a clear refusal is fine
        if (caught != null)
        {
            Console.WriteLine($"ToLocalQueue under ServerlessWithOutbox: {caught.GetType().FullName}: {caught.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // 3. Saga timeouts
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task saga_timeout_is_stored_and_fired_by_a_pass()
    {
        await startTheFunctionAsync(opts => opts.AddSagaType<Explore5044Saga>("explore5044_saga"));

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044StartSaga(id), Token);

        Explore5044Saga.Started.ContainsKey(id).ShouldBeTrue();

        var inbox = await inboxRowsAsync();
        inbox.Count.ShouldBe(1, "The saga's TimeoutMessage was not persisted anywhere");

        await Task.Delay(1500, Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        Explore5044Saga.TimedOut.ContainsKey(id).ShouldBeTrue();
    }

    // -----------------------------------------------------------------------------------------------------------
    // 4. Wolverine.Postgresql's own transactional middleware, no Marten / EF Core
    // -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task transactional_npgsql_handler_cascade(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044TransactionalCascade(id), Token);

        if (receiverIsUp)
        {
            // Sent inline before InvokeAsync returned
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task auto_applied_transaction_npgsql_handler_publish(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(opts => opts.Policies.AutoApplyTransactions());

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044AutoTransactionPublish(id), Token);

        if (receiverIsUp)
        {
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // 7. A container-resolved IMessageBus (what a plain ASP.NET Core minimal API endpoint gets)
    // -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("publish", true)]
    [InlineData("publish", false)]
    [InlineData("send", true)]
    [InlineData("send", false)]
    public async Task scoped_imessagebus_outside_any_handler(string verb, bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        using (var scope = theFunction!.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            if (verb == "publish") await bus.PublishAsync(new Explore5044Remote(id));
            else await bus.SendAsync(new Explore5044Remote(id));
        }

        if (receiverIsUp)
        {
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task plain_minimal_api_endpoint_with_imessagebus(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();

        var httpPort = PortFinder.GetAvailablePort();
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls($"http://localhost:{httpPort}");
        builder.Host.UseWolverine(configureFunction);

        theWebApp = builder.Build();
        theWebApp.MapPost("/explore5044/publish", async (Explore5044Remote message, IMessageBus bus) =>
        {
            await bus.PublishAsync(message);
            return "ok";
        });

        await theWebApp.StartAsync(Token);
        theFunction = theWebApp;
        await theWebApp.RebuildAllEnvelopeStorageAsync();

        var id = Guid.NewGuid();
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync($"http://localhost:{httpPort}/explore5044/publish",
            new Explore5044Remote(id), Token);
        response.EnsureSuccessStatusCode();

        if (receiverIsUp)
        {
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // 8. Request/reply and explicit endpoints
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task invoke_async_request_reply_to_a_remote_endpoint()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();

        // There are no listeners in this mode, so nothing in the function can receive the reply
        Exception? caught = null;
        Explore5044RemoteResponse? response = null;
        try
        {
            response = await theFunction!.MessageBus()
                .InvokeAsync<Explore5044RemoteResponse>(new Explore5044RemoteRequest(id), Token, 5.Seconds());
        }
        catch (Exception e)
        {
            caught = e;
        }

        Console.WriteLine(
            $"InvokeAsync<T> remote under ServerlessWithOutbox: response={response}, exception={caught?.GetType().FullName}: {caught?.Message}");

        Explore5044ReceiverHandler.Requests.ContainsKey(id).ShouldBeTrue("The request never reached the receiver");

        // A reply that can never come back should be refused up front, not time out
        (caught is TimeoutException).ShouldBeFalse(
            $"InvokeAsync<T> to a remote endpoint timed out instead of being refused: {caught?.Message}");
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task endpoint_for_send_async(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().EndpointFor(new Uri($"tcp://localhost:{thePort}"))
            .SendAsync(new Explore5044Remote(id));

        if (receiverIsUp)
        {
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task endpoint_for_send_async_from_a_transactional_handler(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(opts =>
            opts.Services.AddSingleton(new Explore5044PortHolder(thePort)));

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044EndpointForFromHandler(id), Token);

        if (receiverIsUp)
        {
            (await outboxRowsAsync()).ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
        }
        else
        {
            await shouldBeDeliveredNowOrByAPassAsync(id);
        }
    }
}

/// <summary>
/// RabbitMQ under ServerlessWithOutbox: BroadcastToTopicAsync to a topic exchange, plain send to a queue, and a
/// scheduled send (RabbitMQ does not schedule natively in Wolverine).
/// </summary>
public class serverless_with_outbox_exit_paths_rabbitmq_exploratory_5044 : IAsyncDisposable
{
    private const string SchemaName = "explore5044_exit_rmq";
    private const string Exchange = "explore5044_exit_topics";
    private const string Queue = "explore5044_exit_queue";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost? theFunction;
    private IHost? theReceiver;

    public async ValueTask DisposeAsync()
    {
        if (theFunction != null)
        {
            await theFunction.StopAsync();
            theFunction.Dispose();
        }

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    private async Task startAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044ReceiverHandler));
                opts.ListenToRabbitQueue(Queue);
            }).StartAsync(Token);

        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.UseRabbitMq().AutoProvision();

                opts.PublishMessage<Explore5044Remote>().ToRabbitTopics(Exchange, exchange =>
                {
                    exchange.BindTopic("explore5044.#").ToQueue(Queue);
                });

                opts.PublishMessage<Explore5044RemoteRequest>().ToRabbitQueue(Queue);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theFunction.RebuildAllEnvelopeStorageAsync();
    }

    private static async Task<int> outboxCountAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        var count = await conn.CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync(Token);
        return Convert.ToInt32(count);
    }

    private static async Task shouldBeReceivedAsync<T>(ConcurrentDictionary<Guid, T> received, Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (!received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task broadcast_to_topic_is_sent_inline()
    {
        await startAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().BroadcastToTopicAsync("explore5044.one", new Explore5044Remote(id));

        (await outboxCountAsync()).ShouldBe(0);
        await shouldBeReceivedAsync(Explore5044ReceiverHandler.Received, id);
    }

    [Fact]
    public async Task send_to_queue_from_root_bus_is_sent_inline()
    {
        await startAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().SendAsync(new Explore5044RemoteRequest(id));

        (await outboxCountAsync()).ShouldBe(0);
        await shouldBeReceivedAsync(Explore5044ReceiverHandler.Requests, id);
    }

    [Fact]
    public async Task scheduled_send_to_rabbitmq_is_refused_clearly()
    {
        await startAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await theFunction!.MessageBus().ScheduleAsync(new Explore5044RemoteRequest(Guid.NewGuid()), 1.Seconds()));

        ex.Message.ShouldContain("does not support native scheduling");
    }
}

public record Explore5044Remote(Guid Id);

public record Explore5044RemoteRequest(Guid Id);

public record Explore5044RemoteResponse(Guid Id);

public record Explore5044LocalOnly(Guid Id);

public record Explore5044CascadeLocal(Guid Id);

public record Explore5044CascadeDelayed(Guid Id);

public record Explore5044TransactionalCascade(Guid Id);

public record Explore5044AutoTransactionPublish(Guid Id);

public record Explore5044EndpointForFromHandler(Guid Id);

public record Explore5044PortHolder(int Port);

public static class Explore5044FunctionHandlers
{
    public static Explore5044LocalOnly Handle(Explore5044CascadeLocal message) => new(message.Id);

    [Transactional]
    public static object Handle(Explore5044CascadeDelayed message, NpgsqlConnection connection)
        => new Explore5044Remote(message.Id).DelayedFor(1.Seconds());

    [Transactional]
    public static Explore5044Remote Handle(Explore5044TransactionalCascade message, NpgsqlConnection connection)
        => new(message.Id);

    // Only transactional through AutoApplyTransactions, because it takes an NpgsqlConnection
    public static async Task Handle(Explore5044AutoTransactionPublish message, NpgsqlConnection connection,
        IMessageContext context)
    {
        await context.PublishAsync(new Explore5044Remote(message.Id));
    }

    [Transactional]
    public static async Task Handle(Explore5044EndpointForFromHandler message, NpgsqlConnection connection,
        IMessageContext context, Explore5044PortHolder port)
    {
        await context.EndpointFor(new Uri($"tcp://localhost:{port.Port}")).SendAsync(new Explore5044Remote(message.Id));
    }
}

public static class Explore5044LocalOnlyHandler
{
    public static readonly ConcurrentDictionary<Guid, bool> Handled = new();

    public static void Handle(Explore5044LocalOnly message) => Handled[message.Id] = true;
}

public static class Explore5044ReceiverHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Received = new();
    public static readonly ConcurrentDictionary<Guid, bool> Requests = new();

    public static void Handle(Explore5044Remote message) => Received.AddOrUpdate(message.Id, 1, (_, n) => n + 1);

    public static Explore5044RemoteResponse Handle(Explore5044RemoteRequest message)
    {
        Requests[message.Id] = true;
        return new Explore5044RemoteResponse(message.Id);
    }
}

public record Explore5044StartSaga(Guid Id);

public record Explore5044SagaTimeout(Guid Id) : TimeoutMessage(1.Seconds());

public class Explore5044Saga : Saga
{
    public static readonly ConcurrentDictionary<Guid, bool> Started = new();
    public static readonly ConcurrentDictionary<Guid, bool> TimedOut = new();

    public Guid Id { get; set; }

    public static (Explore5044Saga, Explore5044SagaTimeout) Start(Explore5044StartSaga command)
    {
        Started[command.Id] = true;
        return (new Explore5044Saga { Id = command.Id }, new Explore5044SagaTimeout(command.Id));
    }

    public void Handle(Explore5044SagaTimeout timeout)
    {
        TimedOut[timeout.Id] = true;
        MarkCompleted();
    }
}
