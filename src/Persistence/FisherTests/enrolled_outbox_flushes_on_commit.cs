using System.Collections.Concurrent;
using Fisher;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace FisherTests;

/// <summary>
/// A session the application opened itself and enrolled in <see cref="IFisherOutbox" /> has to flush when it
/// commits, as <c>MartenTests.MartenOutbox_end_to_end</c> always has. It never did: Fisher takes a session's
/// listeners only when the session is opened, so Enroll fell back to a transaction participant that did nothing
/// after the commit. Nothing called FlushOutgoingMessagesAsync, which left a durable message in a row owned by
/// this live node, where the durability agent does not look for it (it recovers owner 0), and a buffered message
/// in memory only, where it was lost. The Fisher twin of <c>PolecatTests.enrolled_outbox_flushes_on_commit</c>.
/// </summary>
/// <remarks>
/// The durability agent is off in these tests, so the only thing that can deliver a message is the flush.
/// </remarks>
public class enrolled_outbox_flushes_on_commit : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = 15.Seconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(enrolled_outbox_flushes_on_commit));

        var port = PortFinder.GetAvailablePort();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(EnrolledOutboxHandler))
                    .IncludeType(typeof(EnrolledInboxHandler));

                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.ListenAtPort(port).UseDurableInbox();
                opts.PublishMessage<EnrolledDurableExternal>().ToPort(port).UseDurableOutbox();
                opts.PublishMessage<EnrolledDurableLocal>().ToLocalQueue("enrolled-durable").UseDurableInbox();
                opts.PublishMessage<EnrolledBufferedLocal>().ToLocalQueue("enrolled-buffered").BufferedInMemory();
                opts.PublishMessage<EnrolledInboxMessage>().ToLocalQueue("enrolled-inbox").UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private async Task publishThroughAnEnrolledSessionAsync(object message)
    {
        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IFisherOutbox>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        outbox.Enroll(session);

        session.Store(new EnrolledOutboxDoc { Id = Guid.NewGuid() });
        await outbox.PublishAsync(message);

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task a_durable_message_to_an_external_endpoint_goes_out_on_commit()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledDurableExternal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_durable_local_message_goes_out_on_commit()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledDurableLocal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_buffered_message_goes_out_on_commit_rather_than_being_lost()
    {
        var id = Guid.NewGuid();

        await publishThroughAnEnrolledSessionAsync(new EnrolledBufferedLocal(id));

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_save_holding_nothing_but_a_buffered_message_still_flushes()
    {
        var id = Guid.NewGuid();

        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IFisherOutbox>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        outbox.Enroll(session);

        // Writes no row, so the session has no work of its own for SaveChangesAsync to find
        await outbox.PublishAsync(new EnrolledBufferedLocal(id));
        await session.SaveChangesAsync(Token);

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_session_opened_from_the_store_rather_than_the_container_flushes_too()
    {
        var id = Guid.NewGuid();

        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IFisherOutbox>();

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        outbox.Enroll(session);

        session.Store(new EnrolledOutboxDoc { Id = Guid.NewGuid() });
        await outbox.PublishAsync(new EnrolledBufferedLocal(id));

        await session.SaveChangesAsync(Token);

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task a_commit_that_fails_sends_nothing()
    {
        var id = Guid.NewGuid();

        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IFisherOutbox>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        outbox.Enroll(session);

        session.Store(new EnrolledOutboxDoc { Id = Guid.NewGuid() });
        await outbox.PublishAsync(new EnrolledBufferedLocal(id));
        session.AddTransactionParticipant(new RollBackTheTransaction());

        await Should.ThrowAsync<RolledBackOnPurpose>(() => session.SaveChangesAsync(Token));

        await Task.Delay(1.Seconds(), Token);
        EnrolledDeliveries.WasReceived(id).ShouldBeFalse();
    }

    [Fact]
    public async Task an_outboxed_session_holding_nothing_but_a_buffered_message_still_flushes()
    {
        var id = Guid.NewGuid();

        var context = new MessageContext(theHost.GetRuntime());
        await using var session = theHost.Services.GetRequiredService<OutboxedSessionFactory>().OpenSession(context);

        await context.PublishAsync(new EnrolledBufferedLocal(id));
        await session.SaveChangesAsync(Token);

        await EnrolledDeliveries.WaitFor(id, DeliveryTimeout);
    }

    [Fact]
    public async Task an_outboxed_session_marks_the_incoming_envelope_handled_once()
    {
        // A trigger because both statements are in one transaction and the second changes nothing a query
        // could see afterwards
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Token);

        await executeAsync(conn, "create table handled_marks (id text not null)");
        await executeAsync(conn,
            $"create trigger record_handled_marks after update on {DatabaseConstants.IncomingTable} " +
            $"when new.status = '{EnvelopeStatus.Handled}' begin insert into handled_marks (id) values (new.id); end");

        var id = Guid.NewGuid();
        await theHost.SendMessageAndWaitAsync(new EnrolledInboxMessage(id));

        await using var count = conn.CreateCommand();
        count.CommandText = "select count(*) from handled_marks";

        (await count.ExecuteScalarAsync(Token)).ShouldBe(1L);
    }

    private static async Task executeAsync(SqliteConnection conn, string sql)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}

public class EnrolledOutboxDoc
{
    public Guid Id { get; set; }
}

public record EnrolledDurableExternal(Guid Id);

public record EnrolledDurableLocal(Guid Id);

public record EnrolledBufferedLocal(Guid Id);

public record EnrolledInboxMessage(Guid Id);

public class RolledBackOnPurpose : Exception;

public class RollBackTheTransaction : ITransactionParticipant
{
    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        => throw new RolledBackOnPurpose();
}

public static class EnrolledOutboxHandler
{
    public static void Handle(EnrolledDurableExternal message) => EnrolledDeliveries.Received(message.Id);

    public static void Handle(EnrolledDurableLocal message) => EnrolledDeliveries.Received(message.Id);

    public static void Handle(EnrolledBufferedLocal message) => EnrolledDeliveries.Received(message.Id);
}

public static class EnrolledInboxHandler
{
    // The IDocumentSession is the point: AutoApplyTransactions commits it through OutboxedSessionFactory
    public static void Handle(EnrolledInboxMessage message, IDocumentSession session)
    {
        session.Store(new EnrolledOutboxDoc { Id = message.Id });
    }
}

public static class EnrolledDeliveries
{
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _deliveries = new();

    private static TaskCompletionSource deliveryOf(Guid id)
    {
        return _deliveries.GetOrAdd(id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public static void Received(Guid id) => deliveryOf(id).TrySetResult();

    public static Task WaitFor(Guid id, TimeSpan timeout) => deliveryOf(id).Task.WaitAsync(timeout);

    public static bool WasReceived(Guid id) => deliveryOf(id).Task.IsCompleted;
}
