using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace MartenTests;

/// <summary>
/// GH-5046. Marten under DurabilityMode.ServerlessWithOutbox: a message this application handles itself is written
/// to the inbox in the Marten session's transaction, owned by any node (0), and the durability pass handles it. A
/// scheduled cascade, local or to a transport that cannot schedule natively (TCP), is released by the pass when due,
/// exactly once.
/// </summary>
public class serverless_with_outbox_local_messages_5046 : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "gh5046_marten";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
    private IHost theHost = null!;
    private IHost? theReceiver;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MartenGh5046Handlers));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<MartenGh5046Remote>().ToPort(thePort);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    private async Task<List<(string Status, int OwnerId, string MessageType)>> waitingInboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new List<(string, int, string)>();
        await using var reader = await conn
            .CreateCommand(
                $"select status, owner_id, message_type from {SchemaName}.{DatabaseConstants.IncomingTable} where status != '{EnvelopeStatus.Handled}'")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows.Add((await reader.GetFieldValueAsync<string>(0, Token), await reader.GetFieldValueAsync<int>(1, Token),
                await reader.GetFieldValueAsync<string>(2, Token)));
        }

        return rows;
    }

    private async Task<long> outboxCountAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        return Convert.ToInt64(await conn
            .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync(Token));
    }

    private async Task singleRowWaitingForAPassAsync(EnvelopeStatus status, string messageType)
    {
        var row = (await waitingInboxRowsAsync()).Single();
        row.Status.ShouldBe(status.ToString());
        row.OwnerId.ShouldBe(TransportConstants.AnyNode);
        row.MessageType.ShouldBe(messageType);
    }

    [Fact]
    public async Task a_local_cascade_is_written_in_the_session_transaction_and_handled_by_a_pass()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new MartenGh5046Command(id), Token);

        await using (var query = theHost.DocumentStore().QuerySession())
        {
            (await query.LoadAsync<MartenGh5046Doc>(id, Token)).ShouldNotBeNull();
        }

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Incoming, typeof(MartenGh5046Local).ToMessageTypeName());
        MartenGh5046Handlers.Handled.ContainsKey(id).ShouldBeFalse();

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.IncomingRecovered.ShouldBe(1);
        MartenGh5046Handlers.Handled[id].ShouldBe(1);
        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_local_cascade_is_rolled_back_with_the_session()
    {
        var id = Guid.NewGuid();

        await Should.ThrowAsync<DivideByZeroException>(async () =>
            await theHost.MessageBus().InvokeAsync(new MartenGh5046CommandThenFail(id), Token));

        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_scheduled_local_cascade_is_released_by_a_pass_when_due()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new MartenGh5046ScheduleLocal(id), Token);

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Scheduled, typeof(MartenGh5046Local).ToMessageTypeName());

        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.ScheduledReleased.ShouldBe(0);

        await Task.Delay(1500, Token);

        var due = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        due.ScheduledReleased.ShouldBe(1);
        MartenGh5046Handlers.Handled[id].ShouldBe(1);
        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    // The in-process version of scenario 4 on explore/pr-5044-recovery
    [Fact]
    public async Task a_scheduled_cascade_to_tcp_is_stored_and_released_once_when_due()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MartenGh5046Receiver));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);

        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new MartenGh5046ScheduleRemote(id), Token);

        await using (var query = theHost.DocumentStore().QuerySession())
        {
            (await query.LoadAsync<MartenGh5046Doc>(id, Token)).ShouldNotBeNull();
        }

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Scheduled, TransportConstants.ScheduledEnvelope);
        (await outboxCountAsync()).ShouldBe(0);

        // Not due yet
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.ScheduledReleased.ShouldBe(0);

        // Nothing in memory delivers it while the function is still alive
        await Task.Delay(2.Seconds(), Token);
        MartenGh5046Receiver.Received.ContainsKey(id).ShouldBeFalse();

        var due = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        due.ScheduledReleased.ShouldBe(1);

        var timeout = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (!MartenGh5046Receiver.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }

        var again = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        again.ScheduledReleased.ShouldBe(0);

        await Task.Delay(500, Token);
        MartenGh5046Receiver.Received[id].ShouldBe(1);
        (await waitingInboxRowsAsync()).ShouldBeEmpty();
        (await outboxCountAsync()).ShouldBe(0);
    }
}

public class MartenGh5046Doc
{
    public Guid Id { get; set; }
}

public record MartenGh5046Command(Guid Id);

public record MartenGh5046CommandThenFail(Guid Id);

public record MartenGh5046ScheduleLocal(Guid Id);

public record MartenGh5046ScheduleRemote(Guid Id);

public record MartenGh5046Local(Guid Id);

public record MartenGh5046Remote(Guid Id);

public static class MartenGh5046Handlers
{
    public static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public static MartenGh5046Local Handle(MartenGh5046Command command, IDocumentSession session)
    {
        session.Store(new MartenGh5046Doc { Id = command.Id });
        return new MartenGh5046Local(command.Id);
    }

    public static async Task Handle(MartenGh5046CommandThenFail command, IDocumentSession session,
        IMessageContext context)
    {
        session.Store(new MartenGh5046Doc { Id = command.Id });
        await context.PublishAsync(new MartenGh5046Local(command.Id));
        throw new DivideByZeroException();
    }

    public static object Handle(MartenGh5046ScheduleLocal command, IDocumentSession session)
    {
        session.Store(new MartenGh5046Doc { Id = command.Id });
        return new MartenGh5046Local(command.Id).DelayedFor(1.Seconds());
    }

    public static object Handle(MartenGh5046ScheduleRemote command, IDocumentSession session)
    {
        session.Store(new MartenGh5046Doc { Id = command.Id });
        return new MartenGh5046Remote(command.Id).DelayedFor(1.Seconds());
    }

    public static void Handle(MartenGh5046Local message) => Handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}

public static class MartenGh5046Receiver
{
    public static readonly ConcurrentDictionary<Guid, int> Received = new();

    public static void Handle(MartenGh5046Remote message) => Received.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}
