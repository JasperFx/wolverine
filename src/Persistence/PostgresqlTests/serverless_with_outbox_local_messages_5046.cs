using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
/// GH-5046. There are no local queues under DurabilityMode.ServerlessWithOutbox, so the messages this application
/// handles itself, its locally scheduled messages and its saga timeouts used to be silently dropped. They are written
/// to the durable inbox now, owned by any node (0), and the durability pass executes them: at once when they are
/// immediately runnable, when due when they are scheduled.
/// </summary>
public class serverless_with_outbox_local_messages_5046 : IAsyncDisposable
{
    private const string SchemaName = "gh5046_local_pg";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
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

    private async Task startTheFunctionAsync(Action<WolverineOptions>? configure = null)
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Gh5046FunctionHandlers))
                    .IncludeType(typeof(Gh5046LocalHandler));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                opts.PublishMessage<Gh5046Remote>().ToPort(thePort);

                opts.Services.AddSingleton(new Gh5046Port(thePort));
                opts.Services.AddResourceSetupOnStartup();

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
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5046ReceiverHandler));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    // The inbox rows still waiting to run. Handled rows are only kept for deduplication
    private static async Task<List<(Guid Id, string Status, int OwnerId, string MessageType)>> inboxAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new List<(Guid, string, int, string)>();
        await using var reader = await conn
            .CreateCommand(
                $"select id, status, owner_id, message_type from {SchemaName}.{DatabaseConstants.IncomingTable} where status != '{EnvelopeStatus.Handled}'")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows.Add((await reader.GetFieldValueAsync<Guid>(0, Token), await reader.GetFieldValueAsync<string>(1, Token),
                await reader.GetFieldValueAsync<int>(2, Token), await reader.GetFieldValueAsync<string>(3, Token)));
        }

        return rows;
    }

    private static async Task<long> outboxCountAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        return Convert.ToInt64(await conn
            .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync(Token));
    }

    // One row waiting for a pass, owned by any node, and nothing run yet
    private static async Task shouldBeWaitingForAPassAsync(EnvelopeStatus status, string messageType)
    {
        var inbox = await inboxAsync();
        inbox.Count.ShouldBe(1);

        var row = inbox.Single();
        row.Status.ShouldBe(status.ToString());
        row.OwnerId.ShouldBe(TransportConstants.AnyNode);
        row.MessageType.ShouldBe(messageType);
    }

    private async Task shouldBeHandledByTheNextPassAsync(Guid id)
    {
        Gh5046LocalHandler.Handled.ContainsKey(id).ShouldBeFalse("Handled before any durability pass");
        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Incoming, typeof(Gh5046Local).ToMessageTypeName());

        var report = await theFunction!.RunDurabilityPassAsync(token: Token);

        report.IncomingRecovered.ShouldBe(1);
        Gh5046LocalHandler.Handled[id].ShouldBe(1);
        (await inboxAsync()).ShouldBeEmpty();
    }

    private static async Task shouldBeReceivedAsync(Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (!Gh5046ReceiverHandler.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task publish_async_of_a_locally_handled_message_is_stored_and_handled_by_a_pass()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().PublishAsync(new Gh5046Local(id));

        await shouldBeHandledByTheNextPassAsync(id);
    }

    [Fact]
    public async Task send_async_of_a_locally_handled_message_is_stored_and_handled_by_a_pass()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().SendAsync(new Gh5046Local(id));

        await shouldBeHandledByTheNextPassAsync(id);
    }

    [Fact]
    public async Task a_cascaded_message_with_a_local_handler_is_stored_and_handled_by_a_pass()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Gh5046CascadeLocal(id), Token);

        await shouldBeHandledByTheNextPassAsync(id);
    }

    [Fact]
    public async Task a_local_cascade_from_a_transactional_handler_is_written_in_its_transaction()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Gh5046TransactionalCascadeLocal(id), Token);

        await shouldBeHandledByTheNextPassAsync(id);
    }

    [Fact]
    public async Task a_local_cascade_is_rolled_back_with_its_transaction()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await Should.ThrowAsync<DivideByZeroException>(async () =>
            await theFunction!.MessageBus().InvokeAsync(new Gh5046TransactionalCascadeThenFail(id), Token));

        (await inboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_explicit_route_to_a_local_queue_is_stored_and_handled_by_a_pass()
    {
        await startTheFunctionAsync(opts => opts.PublishMessage<Gh5046Local>().ToLocalQueue("gh5046"));

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().PublishAsync(new Gh5046Local(id));

        await shouldBeHandledByTheNextPassAsync(id);
    }

    [Fact]
    public async Task schedule_async_of_a_locally_handled_message_is_stored_and_released_by_a_pass_when_due()
    {
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().ScheduleAsync(new Gh5046Local(id), 1.Seconds());

        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, typeof(Gh5046Local).ToMessageTypeName());

        // Not due yet
        var early = await theFunction!.RunDurabilityPassAsync(token: Token);
        early.ScheduledReleased.ShouldBe(0);
        Gh5046LocalHandler.Handled.ContainsKey(id).ShouldBeFalse();

        await Task.Delay(1500, Token);

        var due = await theFunction!.RunDurabilityPassAsync(token: Token);
        due.ScheduledReleased.ShouldBe(1);
        Gh5046LocalHandler.Handled[id].ShouldBe(1);
        (await inboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task saga_timeout_is_stored_and_fired_by_a_pass()
    {
        await startTheFunctionAsync(opts =>
        {
            opts.Discovery.IncludeType(typeof(Gh5046Saga));
            opts.AddSagaType<Gh5046Saga>("gh5046_saga");
        });

        await theFunction!.ResetResourceState(Token);

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Gh5046StartSaga(id), Token);

        Gh5046Saga.Started.ContainsKey(id).ShouldBeTrue();
        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, typeof(Gh5046SagaTimeout).ToMessageTypeName());

        await Task.Delay(1500, Token);
        var report = await theFunction!.RunDurabilityPassAsync(token: Token);

        report.ScheduledReleased.ShouldBe(1);
        Gh5046Saga.TimedOut.ContainsKey(id).ShouldBeTrue();
    }

    [Fact]
    public async Task schedule_async_to_tcp_is_stored_and_released_by_a_pass_exactly_once()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().ScheduleAsync(new Gh5046Remote(id), 1.Seconds());

        // Wrapped for the pass, as TCP cannot schedule natively
        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, TransportConstants.ScheduledEnvelope);
        (await outboxCountAsync()).ShouldBe(0);

        await shouldBeReleasedOnceByAPassAsync(id);
    }

    [Fact]
    public async Task a_scheduled_cascade_to_tcp_from_a_transactional_handler_is_released_by_a_pass_exactly_once()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Gh5046CascadeDelayedRemote(id), Token);

        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, TransportConstants.ScheduledEnvelope);
        (await outboxCountAsync()).ShouldBe(0);

        await shouldBeReleasedOnceByAPassAsync(id);
    }

    // GH-5052
    [Fact]
    public async Task endpoint_for_scheduled_send_to_tcp_is_stored_for_any_node_and_released_by_a_pass_exactly_once()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().EndpointFor(new Uri($"tcp://localhost:{thePort}"))
            .SendAsync(new Gh5046Remote(id), new DeliveryOptions { ScheduleDelay = 1.Seconds() });

        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, TransportConstants.ScheduledEnvelope);

        // No durable local queue left over from before the local transport was removed
        var runtime = theFunction!.GetRuntime();
        runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri).ShouldNotBeOfType<DurableLocalQueue>();
        runtime.Endpoints.ActiveSendingAgents().OfType<DurableLocalQueue>().ShouldBeEmpty();

        await shouldBeReleasedOnceByAPassAsync(id);
    }

    // GH-5052
    [Fact]
    public async Task endpoint_for_scheduled_send_from_a_transactional_handler_is_released_by_a_pass_exactly_once()
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Gh5046EndpointForDelayed(id), Token);

        await shouldBeWaitingForAPassAsync(EnvelopeStatus.Scheduled, TransportConstants.ScheduledEnvelope);

        await shouldBeReleasedOnceByAPassAsync(id);
    }

    private async Task shouldBeReleasedOnceByAPassAsync(Guid id)
    {
        // Keep the function alive past the due time without a pass: anything delivered now came from an in-memory
        // timer, which a frozen serverless function would never run
        await Task.Delay(2.Seconds(), Token);
        Gh5046ReceiverHandler.Received.ContainsKey(id).ShouldBeFalse("Delivered without any durability pass");

        var report = await theFunction!.RunDurabilityPassAsync(token: Token);
        report.ScheduledReleased.ShouldBe(1);

        await shouldBeReceivedAsync(id);

        var again = await theFunction!.RunDurabilityPassAsync(token: Token);
        again.ScheduledReleased.ShouldBe(0);

        await Task.Delay(500, Token);
        Gh5046ReceiverHandler.Received[id].ShouldBe(1);
        (await inboxAsync()).ShouldBeEmpty();
        (await outboxCountAsync()).ShouldBe(0);
    }
}

/// <summary>
/// GH-5046. RabbitMQ does not schedule natively in Wolverine, so a scheduled send to it under ServerlessWithOutbox
/// is wrapped and stored for the durability pass to release, as in the other modes.
/// </summary>
public class serverless_with_outbox_rabbitmq_scheduling_5046 : IAsyncDisposable
{
    private const string SchemaName = "gh5046_local_rmq";
    private const string Queue = "gh5046_scheduled";

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

    private static async Task<long> countAsync(string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        return Convert.ToInt64(await conn.CreateCommand($"select count(*) from {SchemaName}.{table}")
            .ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task a_scheduled_send_to_rabbitmq_is_stored_and_released_by_a_pass()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5046ReceiverHandler));
                opts.ListenToRabbitQueue(Queue);
            }).StartAsync(Token);

        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<Gh5046Remote>().ToRabbitQueue(Queue);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theFunction.RebuildAllEnvelopeStorageAsync();

        var id = Guid.NewGuid();
        await theFunction.MessageBus().ScheduleAsync(new Gh5046Remote(id), 1.Seconds());

        (await countAsync(DatabaseConstants.IncomingTable)).ShouldBe(1);
        (await countAsync(DatabaseConstants.OutgoingTable)).ShouldBe(0);

        await Task.Delay(1500, Token);
        var report = await theFunction.RunDurabilityPassAsync(token: Token);
        report.ScheduledReleased.ShouldBe(1);

        var timeout = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (!Gh5046ReceiverHandler.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }

        (await countAsync(DatabaseConstants.OutgoingTable)).ShouldBe(0);
    }
}

public record Gh5046Local(Guid Id);

public record Gh5046Remote(Guid Id);

public record Gh5046CascadeLocal(Guid Id);

public record Gh5046TransactionalCascadeLocal(Guid Id);

public record Gh5046TransactionalCascadeThenFail(Guid Id);

public record Gh5046CascadeDelayedRemote(Guid Id);

public record Gh5046EndpointForDelayed(Guid Id);

public record Gh5046Port(int Port);

public static class Gh5046FunctionHandlers
{
    public static Gh5046Local Handle(Gh5046CascadeLocal message) => new(message.Id);

    [Transactional]
    public static Gh5046Local Handle(Gh5046TransactionalCascadeLocal message, NpgsqlConnection connection)
        => new(message.Id);

    [Transactional]
    public static async Task Handle(Gh5046TransactionalCascadeThenFail message, NpgsqlConnection connection,
        IMessageContext context)
    {
        await context.PublishAsync(new Gh5046Local(message.Id));
        throw new DivideByZeroException();
    }

    [Transactional]
    public static object Handle(Gh5046CascadeDelayedRemote message, NpgsqlConnection connection)
        => new Gh5046Remote(message.Id).DelayedFor(1.Seconds());

    [Transactional]
    public static async Task Handle(Gh5046EndpointForDelayed message, NpgsqlConnection connection,
        IMessageContext context, Gh5046Port port)
    {
        await context.EndpointFor(new Uri($"tcp://localhost:{port.Port}"))
            .SendAsync(new Gh5046Remote(message.Id), new DeliveryOptions { ScheduleDelay = 1.Seconds() });
    }
}

public static class Gh5046LocalHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public static void Handle(Gh5046Local message) => Handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}

public static class Gh5046ReceiverHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Received = new();

    public static void Handle(Gh5046Remote message) => Received.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}

public record Gh5046StartSaga(Guid Id);

public record Gh5046SagaTimeout(Guid Id) : TimeoutMessage(1.Seconds());

public class Gh5046Saga : Saga
{
    public static readonly ConcurrentDictionary<Guid, bool> Started = new();
    public static readonly ConcurrentDictionary<Guid, bool> TimedOut = new();

    public Guid Id { get; set; }

    public static (Gh5046Saga, Gh5046SagaTimeout) Start(Gh5046StartSaga command)
    {
        Started[command.Id] = true;
        return (new Gh5046Saga { Id = command.Id }, new Gh5046SagaTimeout(command.Id));
    }

    public void Handle(Gh5046SagaTimeout timeout)
    {
        TimedOut[timeout.Id] = true;
        MarkCompleted();
    }
}
