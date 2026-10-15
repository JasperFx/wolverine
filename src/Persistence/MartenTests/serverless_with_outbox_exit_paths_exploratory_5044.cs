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
using Wolverine.Marten.Publishing;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace MartenTests;

/// <summary>
/// Exploratory tests for PR #5044 (DurabilityMode.ServerlessWithOutbox) with Marten: IMartenOutbox and
/// OutboxedSessionFactory used directly outside of any handler (as from a plain minimal API endpoint, which
/// resolves them from its request scope exactly like this), and a handler with the Marten transactional
/// middleware. Sends go over TCP so a real DurableSendingAgent is in play.
/// </summary>
public class serverless_with_outbox_exit_paths_exploratory_5044 : IAsyncDisposable
{
    private const string SchemaName = "explore5044_exit_marten";

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

    private async Task startTheFunctionAsync()
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 100.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044MartenHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<Explore5044MartenCascaded>().ToPort(thePort);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theFunction.ResetResourceState();
    }

    private async Task startTheReceiverAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044MartenReceiver));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    private static async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new Dictionary<Guid, int>();
        await using var reader = await conn
            .CreateCommand($"select id, owner_id from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows[await reader.GetFieldValueAsync<Guid>(0, Token)] = await reader.GetFieldValueAsync<int>(1, Token);
        }

        return rows;
    }

    private static async Task shouldBeReceivedAsync(Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (!Explore5044MartenReceiver.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }
    }

    private async Task assertDeliveredAsync(Guid id, bool receiverIsUp)
    {
        var rows = await outboxRowsAsync();

        if (receiverIsUp)
        {
            rows.ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
            return;
        }

        rows.Count.ShouldBe(1);
        rows.Values.Single().ShouldBe(TransportConstants.AnyNode);

        await startTheReceiverAsync();
        await Task.Delay(200, Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        await shouldBeReceivedAsync(id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task marten_outbox_outside_a_handler(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        using (var scope = theFunction!.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            outbox.Enroll(session);

            session.Store(new Item { Id = id });
            await outbox.PublishAsync(new Explore5044MartenCascaded(id));
            await session.SaveChangesAsync(Token);
        }

        await assertDeliveredAsync(id, receiverIsUp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task outboxed_session_factory_outside_a_handler(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        using (var scope = theFunction!.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<OutboxedSessionFactory>();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await using var session = factory.OpenSession(bus);

            session.Store(new Item { Id = id });
            await bus.PublishAsync(new Explore5044MartenCascaded(id));
            await session.SaveChangesAsync(Token);
        }

        await assertDeliveredAsync(id, receiverIsUp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task marten_handler_cascade(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044MartenCommand(id), Token);

        await assertDeliveredAsync(id, receiverIsUp);
    }
}

public record Explore5044MartenCommand(Guid Id);

public record Explore5044MartenCascaded(Guid Id);

public static class Explore5044MartenHandler
{
    public static Explore5044MartenCascaded Handle(Explore5044MartenCommand command, IDocumentSession session)
    {
        session.Store(new Item { Id = command.Id });
        return new Explore5044MartenCascaded(command.Id);
    }
}

public static class Explore5044MartenReceiver
{
    public static readonly ConcurrentDictionary<Guid, bool> Received = new();

    public static void Handle(Explore5044MartenCascaded message) => Received[message.Id] = true;
}
