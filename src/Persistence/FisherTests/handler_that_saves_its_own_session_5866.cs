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
using Wolverine.Attributes;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.Persistence;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace FisherTests;

public class handler_that_saves_its_own_session_5866 : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        SelfSavingCounts.Reset();

        theDatabase = Servers.CreateDatabase(nameof(handler_that_saves_its_own_session_5866));

        var port = PortFinder.GetAvailablePort();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SelfSavingHandlers));

                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.ListenAtPort(port).UseDurableInbox();
                opts.PublishMessage<SelfSavingExternalTarget>().ToPort(port).UseDurableOutbox();
                opts.PublishMessage<SelfSavingLocalTarget>().ToLocalQueue("self-saving-target").UseDurableInbox();
                opts.PublishMessage<SelfSavingScheduled>().ToLocalQueue("self-saving-scheduled").UseDurableInbox();
                opts.PublishMessage<SelfSavingEager>().ToLocalQueue("self-saving-eager").BufferedInMemory();
                opts.PublishMessage<SelfSavingFromDurableInbox>().ToLocalQueue("self-saving-inbox").UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task publishing_to_a_durable_local_queue_keeps_the_work_after_the_handlers_own_save()
    {
        var message = new PublishToDurableLocalQueue(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        tracked.Discarded.Envelopes().ShouldBeEmpty();
        SelfSavingCounts.Attempts(message.SavedId).ShouldBe(1);
        await shouldHaveCommittedAsync(message.SavedId, message.AfterId);
        SelfSavingCounts.Deliveries(message.SavedId).ShouldBe(1);
    }

    [Fact]
    public async Task publishing_to_an_external_durable_outbox_keeps_the_work_after_the_handlers_own_save()
    {
        var message = new PublishToDurableOutbox(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected().IncludeExternalTransports()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        tracked.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
        await shouldHaveCommittedAsync(message.SavedId, message.AfterId);
        SelfSavingCounts.Deliveries(message.SavedId).ShouldBe(1);

        await waitForNoOutgoingRowsAsync();
    }

    [Fact]
    public async Task eager_idempotency_keeps_the_work_after_the_handlers_own_save()
    {
        var message = new SelfSavingEager(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        tracked.Discarded.Envelopes().ShouldBeEmpty();
        SelfSavingCounts.Attempts(message.SavedId).ShouldBe(1);
        await shouldHaveCommittedAsync(message.SavedId, message.AfterId);
    }

    [Fact]
    public async Task a_message_from_a_durable_inbox_is_marked_handled_once_after_the_handlers_own_save()
    {
        var message = new SelfSavingFromDurableInbox(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        SelfSavingCounts.Attempts(message.SavedId).ShouldBe(1);
        await shouldHaveCommittedAsync(message.SavedId, message.AfterId);

        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Token);
        await using var query = conn.CreateCommand();
        query.CommandText =
            $"select status from {DatabaseConstants.IncomingTable} where message_type = '{typeof(SelfSavingFromDurableInbox).ToMessageTypeName()}'";
        await using var reader = await query.ExecuteReaderAsync(Token);

        var statuses = new List<string>();
        while (await reader.ReadAsync(Token)) statuses.Add(reader.GetString(0));

        statuses.ShouldBe([EnvelopeStatus.Handled.ToString()]);
    }

    [Fact]
    public async Task a_save_that_rolls_back_does_not_stop_the_next_save_writing_the_envelope()
    {
        var context = new MessageContext(theHost.GetRuntime());
        await using var session = theHost.Services.GetRequiredService<OutboxedSessionFactory>().OpenSession(context);

        await context.ScheduleAsync(new SelfSavingScheduled(Guid.NewGuid()), 1.Hours());

        // Enlisted after the envelope, so the envelope's row is written before this rolls the transaction back
        session.AddTransactionParticipant(new RollBackTheFirstSave());
        await Should.ThrowAsync<RolledBackOnPurpose>(() => session.SaveChangesAsync(Token));

        await session.SaveChangesAsync(Token);

        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Token);
        await using var count = conn.CreateCommand();
        count.CommandText =
            $"select count(*) from {DatabaseConstants.IncomingTable} where message_type = '{typeof(SelfSavingScheduled).ToMessageTypeName()}'";

        (await count.ExecuteScalarAsync(Token)).ShouldBe(1L);
    }

    private async Task shouldHaveCommittedAsync(Guid savedId, Guid afterId)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.LoadAsync<SelfSavingDoc>(savedId, Token)).ShouldNotBeNull();
        (await session.LoadAsync<SelfSavingDoc>(afterId, Token)).ShouldNotBeNull();
    }

    private async Task waitForNoOutgoingRowsAsync()
    {
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Token);
        await using var count = conn.CreateCommand();
        count.CommandText = $"select count(*) from {DatabaseConstants.OutgoingTable}";

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        object? rows;
        do
        {
            rows = await count.ExecuteScalarAsync(Token);
            if (rows is 0L) return;
            await Task.Delay(100.Milliseconds(), Token);
        } while (DateTimeOffset.UtcNow < deadline);

        rows.ShouldBe(0L);
    }
}

public record PublishToDurableLocalQueue(Guid SavedId, Guid AfterId);

public record PublishToDurableOutbox(Guid SavedId, Guid AfterId);

public record SelfSavingEager(Guid SavedId, Guid AfterId);

public record SelfSavingFromDurableInbox(Guid SavedId, Guid AfterId);

public record SelfSavingLocalTarget(Guid Id);

public record SelfSavingExternalTarget(Guid Id);

public record SelfSavingScheduled(Guid Id);

public class SelfSavingDoc
{
    public Guid Id { get; set; }
}

public class RollBackTheFirstSave : ITransactionParticipant
{
    private bool _failed;

    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        if (_failed) return Task.CompletedTask;

        _failed = true;
        throw new RolledBackOnPurpose();
    }
}

public static class SelfSavingHandlers
{
    private static async Task saveThenStoreMoreAsync(Guid savedId, Guid afterId, IDocumentSession session)
    {
        SelfSavingCounts.Attempted(savedId);

        session.Store(new SelfSavingDoc { Id = savedId });
        await session.SaveChangesAsync();

        session.Store(new SelfSavingDoc { Id = afterId });
    }

    [Transactional]
    public static async Task Handle(PublishToDurableLocalQueue message, IDocumentSession session, IMessageContext context)
    {
        await context.PublishAsync(new SelfSavingLocalTarget(message.SavedId));
        await saveThenStoreMoreAsync(message.SavedId, message.AfterId, session);
    }

    [Transactional]
    public static async Task Handle(PublishToDurableOutbox message, IDocumentSession session, IMessageContext context)
    {
        await context.PublishAsync(new SelfSavingExternalTarget(message.SavedId));
        await saveThenStoreMoreAsync(message.SavedId, message.AfterId, session);
    }

    [Transactional(IdempotencyStyle.Eager)]
    public static Task Handle(SelfSavingEager message, IDocumentSession session)
        => saveThenStoreMoreAsync(message.SavedId, message.AfterId, session);

    [Transactional]
    public static Task Handle(SelfSavingFromDurableInbox message, IDocumentSession session)
        => saveThenStoreMoreAsync(message.SavedId, message.AfterId, session);

    public static void Handle(SelfSavingLocalTarget message) => SelfSavingCounts.Delivered(message.Id);

    public static void Handle(SelfSavingExternalTarget message) => SelfSavingCounts.Delivered(message.Id);

    public static void Handle(SelfSavingScheduled message)
    {
    }
}

public static class SelfSavingCounts
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, int> _deliveries = new();

    public static void Reset()
    {
        _attempts.Clear();
        _deliveries.Clear();
    }

    public static void Attempted(Guid id) => _attempts.AddOrUpdate(id, 1, (_, n) => n + 1);

    public static void Delivered(Guid id) => _deliveries.AddOrUpdate(id, 1, (_, n) => n + 1);

    public static int Attempts(Guid id) => _attempts.GetValueOrDefault(id);

    public static int Deliveries(Guid id) => _deliveries.GetValueOrDefault(id);
}
