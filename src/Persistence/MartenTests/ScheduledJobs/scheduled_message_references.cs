using IntegrationTests;
using Marten;
using JasperFx;
using Npgsql;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace MartenTests.ScheduledJobs;

public class scheduled_message_references : ScheduledMessageReferenceCompliance
{
    private readonly string _schema = "schedule_refs_" + Guid.NewGuid().ToString("N");
    protected virtual bool partitioned => false;

    protected override void configurePersistence(WolverineOptions options)
    {
        options.Durability.EnableInboxPartitioning = partitioned;
        options.Services.AddMarten(store =>
        {
            store.Connection(Servers.PostgresConnectionString);
            store.DatabaseSchemaName = _schema;
        }).IntegrateWithWolverine();
    }

    [Fact]
    public async Task batched_incoming_storage_retains_reference_and_tenant()
    {
        using var host = await startHost();
        var store = host.Services.GetRequiredService<IMessageStore>();
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(1),
            new DeliveryOptions { ReferenceKey = "account/1", TenantId = "tenant-a" });
        var envelopes = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = (await store.Admin.AllIncomingAsync()).Single();
            envelope.Id = Guid.NewGuid();
            envelopes.Add(envelope);
        }

        await store.Inbox.StoreIncomingAsync(envelopes);
        var results = await store.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { ReferenceKey = "account/1", TenantId = "tenant-a" }, TestContext.Current.CancellationToken);
        results.TotalCount.ShouldBe(4);
        results.Messages.Select(x => x.Id).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public async Task committed_schedule_survives_restart_without_outbox_relay()
    {
        Guid id;
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1" };
        using (var host = await startHost())
        {
            using var scope = host.Services.CreateScope();
            await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            outbox.Enroll(session);
            await outbox.ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(1),
                new DeliveryOptions { ReferenceKey = "account/1" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
            id = (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).Messages.Single().Id;
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using var restarted = await startHost();
        var recovered = restarted.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        (await recovered.QueryAsync(query, TestContext.Current.CancellationToken)).Messages.Single().Id.ShouldBe(id);
        await recovered.RescheduleAsync(id, DateTimeOffset.UtcNow.AddSeconds(-1), TestContext.Current.CancellationToken);
        var observed = await restarted.Services.GetRequiredService<ReferencedWorkObservations>().Started.Task
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        observed.Id.ShouldBe(id);
        observed.ReferenceKey.ShouldBe("account/1");
    }

    [Fact]
    public async Task upgrades_existing_inbox_without_losing_unkeyed_schedules()
    {
        Guid id;
        using (var host = await startHost())
        {
            await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(1));
            id = (await host.Services.GetRequiredService<IMessageStore>().Admin.AllIncomingAsync()).Single().Id;
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"alter table {_schema}.wolverine_incoming_envelopes drop column reference_key, drop column tenant_id, drop column service_name";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        using var upgraded = await startHost();
        var schedules = upgraded.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var legacy = (await schedules.QueryAsync(new ScheduledMessageQuery(), TestContext.Current.CancellationToken)).Messages.Single();
        legacy.Id.ShouldBe(id);
        legacy.ReferenceKey.ShouldBeNull();
        await upgraded.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(1),
            new DeliveryOptions { ReferenceKey = "account/1" });
        (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "account/1" }, TestContext.Current.CancellationToken))
            .TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task promotion_skips_a_schedule_locked_for_management()
    {
        using var host = await startHost(firstPoll: TimeSpan.FromHours(1));
        var store = (IMessageDatabase)host.Services.GetRequiredService<IMessageStore>();
        var due = DateTimeOffset.UtcNow.AddHours(1);
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), due,
            new DeliveryOptions { ReferenceKey = "account/1" });
        await host.MessageBus().ScheduleAsync(new ReferencedWork(2), due,
            new DeliveryOptions { ReferenceKey = "account/2" });
        foreach (var message in (await store.ScheduledMessages.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None)).Messages)
        {
            await store.ScheduledMessages.RescheduleAsync(message.Id, DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        }

        var captured = new List<Envelope>();
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(messages => captured.AddRange(messages)))
            .Returns(ValueTask.CompletedTask);
        await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"select id from {_schema}.wolverine_incoming_envelopes where reference_key = 'account/1' for update";
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        var poll = store.PollForScheduledMessagesAsync(runtime, NullLogger.Instance,
            host.Services.GetRequiredService<DurabilitySettings>(), TestContext.Current.CancellationToken);
        try
        {
            await poll.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            captured.Single().ReferenceKey.ShouldBe("account/2");
        }
        finally
        {
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            await poll.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        await store.ScheduledMessages.CancelAsync(new ScheduledMessageQuery { ReferenceKey = "account/1" },
            TestContext.Current.CancellationToken);
        captured.Clear();
        await store.PollForScheduledMessagesAsync(runtime, NullLogger.Instance,
            host.Services.GetRequiredService<DurabilitySettings>(), TestContext.Current.CancellationToken);
        captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task retry_restores_reference_metadata_on_a_replayed_incoming_row()
    {
        using var host = await startHost();
        var store = host.Services.GetRequiredService<IMessageStore>();
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
            new DeliveryOptions { ReferenceKey = "account/1", TenantId = "tenant-a" });
        var envelope = (await store.Admin.AllIncomingAsync()).Single();
        // The existing SQL dead-letter replay inserts the original body without the new columns.
        await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"update {_schema}.wolverine_incoming_envelopes set reference_key = null, tenant_id = null, service_name = null";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await store.Inbox.ScheduleExecutionAsync(envelope);
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1", TenantId = "tenant-a" };
        (await store.ScheduledMessages.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task schedule_and_reference_commit_with_the_marten_business_write(bool commit)
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1", TenantId = "tenant-a" };
        var accountId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            outbox.Enroll(session);
            session.Store(new ReferencedAccount { Id = accountId });
            await outbox.ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
                new DeliveryOptions { ReferenceKey = "account/1", TenantId = "tenant-a" });
            (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(0);
            if (commit) await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(commit ? 1 : 0);
        await using var verification = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        ((await verification.LoadAsync<ReferencedAccount>(accountId, TestContext.Current.CancellationToken)) != null)
            .ShouldBe(commit);
    }

    [Fact]
    public async Task failed_business_write_rolls_back_the_schedule()
    {
        using var host = await startHost();
        var documents = host.Services.GetRequiredService<IDocumentStore>();
        var existing = new ReferencedAccount { Id = Guid.NewGuid() };
        await using (var setup = documents.LightweightSession())
        {
            setup.Store(existing);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = host.Services.CreateScope();
        await using var session = documents.LightweightSession();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
        outbox.Enroll(session);
        await outbox.ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(1),
            new DeliveryOptions { ReferenceKey = "account/1" });
        var newAccount = new ReferencedAccount { Id = Guid.NewGuid() };
        session.Store(newAccount);
        session.Insert(existing); // A duplicate document insert forces the database transaction to fail.
        await Should.ThrowAsync<DocumentAlreadyExistsException>(() => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "account/1" }, TestContext.Current.CancellationToken))
            .TotalCount.ShouldBe(0);
        await using var verification = documents.QuerySession();
        (await verification.LoadAsync<ReferencedAccount>(newAccount.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verification.LoadAsync<ReferencedAccount>(existing.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

}

public class partitioned_scheduled_message_references : scheduled_message_references
{
    protected override bool partitioned => true;
}

public class ReferencedAccount
{
    public Guid Id { get; set; }
}
