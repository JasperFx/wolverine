using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.RavenDb;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Operations.Indexes;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.RavenDb.Internals;
using Wolverine.Runtime;

namespace RavenDbTests;

[Collection("raven")]
public class scheduled_message_references(DatabaseFixture fixture) : ScheduledMessageReferenceCompliance
{
    private IDocumentStore? _documents;

    protected override void configurePersistence(WolverineOptions options)
    {
        _documents ??= fixture.StartRavenStore();
        options.Services.AddSingleton(_documents);
        options.UseRavenDbPersistence();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task cancellation_does_not_delete_work_changed_after_selection(bool promoted)
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
            new DeliveryOptions { ReferenceKey = "account/1" });

        // Interleave a competing commit exactly between selection and cancellation's save.
        void changeSelectedMessage(object? sender, BeforeDeleteEventArgs args)
        {
            if (args.Entity is not IncomingMessage message) return;
            using var competing = _documents!.OpenSession();
            var current = competing.Load<IncomingMessage>(message.Id);
            if (promoted) current.Status = EnvelopeStatus.Incoming;
            else current.ExecutionTime = DateTimeOffset.UtcNow.AddHours(4);
            competing.SaveChanges();
        }

        _documents!.OnBeforeDelete += changeSelectedMessage;
        try
        {
            await Should.ThrowAsync<ConcurrencyException>(() => schedules.CancelAsync(
                new ScheduledMessageQuery { ReferenceKey = "account/1" }, TestContext.Current.CancellationToken));
        }
        finally
        {
            _documents.OnBeforeDelete -= changeSelectedMessage;
        }

        using var verification = _documents.OpenAsyncSession();
        var remaining = await verification.Query<IncomingMessage>().SingleAsync(TestContext.Current.CancellationToken);
        remaining.Status.ShouldBe(promoted ? EnvelopeStatus.Incoming : EnvelopeStatus.Scheduled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task a_stale_promotion_cannot_dispatch_cancelled_or_rescheduled_work(bool cancel)
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var observations = host.Services.GetRequiredService<ReferencedWorkObservations>();
        var intercepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void cancelBeforePromotion(object? sender, BeforeStoreEventArgs args)
        {
            if (args.Entity is not IncomingMessage { Status: EnvelopeStatus.Incoming, ReferenceKey: "cancel-me" } message) return;
#pragma warning disable VSTHRD103 // RavenDB's synchronous event must hold the promotion until cancellation commits.
            var mutation = cancel
                ? schedules.CancelAsync(new ScheduledMessageQuery { ReferenceKey = "cancel-me" }, CancellationToken.None)
                : schedules.RescheduleAsync(message.EnvelopeId, DateTimeOffset.UtcNow.AddHours(4), CancellationToken.None);
            mutation.GetAwaiter().GetResult();
#pragma warning restore VSTHRD103
            intercepted.TrySetResult();
        }

        _documents!.OnBeforeStore += cancelBeforePromotion;
        try
        {
            await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddMilliseconds(200),
                new DeliveryOptions { ReferenceKey = "cancel-me" });
            await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await host.MessageBus().ScheduleAsync(new ReferencedWork(2), DateTimeOffset.UtcNow.AddMilliseconds(200));
            await observations.Sentinel.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            observations.Received.ShouldNotContain(x => x.ReferenceKey == "cancel-me");
        }
        finally
        {
            _documents.OnBeforeStore -= cancelBeforePromotion;
        }
    }

    [Fact]
    public async Task stale_index_candidates_are_checked_against_the_loaded_schedule()
    {
        using var host = await startHost(firstPoll: TimeSpan.FromSeconds(5));
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var due = DateTimeOffset.UtcNow.AddSeconds(1);
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), due,
            new DeliveryOptions { ReferenceKey = "moved" });
        await host.MessageBus().ScheduleAsync(new ReferencedWork(2), due);
        var selected = (await schedules.QueryAsync(new ScheduledMessageQuery(), TestContext.Current.CancellationToken))
            .Messages.Single(x => x.ReferenceKey == "moved");

        await _documents!.Maintenance.SendAsync(new StopIndexingOperation(), TestContext.Current.CancellationToken);
        try
        {
            // Freeze the index's old due time while the document itself moves into the future.
            using (var session = _documents.OpenAsyncSession())
            {
                var persistence = (RavenDbMessageStore)host.Services.GetRequiredService<IMessageStore>();
                var identity = persistence.IdentityFor(new Envelope { Id = selected.Id, Destination = new Uri(selected.Destination!) });
                var message = await session.LoadAsync<IncomingMessage>(identity, TestContext.Current.CancellationToken);
                message.ExecutionTime = DateTimeOffset.UtcNow.AddHours(4);
                await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var observations = host.Services.GetRequiredService<ReferencedWorkObservations>();
            await observations.Sentinel.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            observations.Received.ShouldNotContain(x => x.ReferenceKey == "moved");
        }
        finally
        {
            await _documents.Maintenance.SendAsync(new StartIndexingOperation(), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task rescheduling_loses_explicitly_if_dispatch_changes_the_selected_document()
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
            new DeliveryOptions { ReferenceKey = "account/1" });
        var selected = (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "account/1" },
            TestContext.Current.CancellationToken)).Messages.Single();

        void promoteBeforeReschedule(object? sender, BeforeStoreEventArgs args)
        {
            if (args.Entity is not IncomingMessage { Status: EnvelopeStatus.Scheduled } message) return;
            using var competing = _documents!.OpenSession();
            var current = competing.Load<IncomingMessage>(message.Id);
            current.Status = EnvelopeStatus.Incoming;
            competing.SaveChanges();
        }

        _documents!.OnBeforeStore += promoteBeforeReschedule;
        try
        {
            await Should.ThrowAsync<ConcurrencyException>(() => schedules.RescheduleAsync(selected.Id,
                DateTimeOffset.UtcNow.AddHours(4), TestContext.Current.CancellationToken));
        }
        finally
        {
            _documents.OnBeforeStore -= promoteBeforeReschedule;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task reference_and_schedule_share_the_business_commit(bool commit)
    {
        using var host = await startHost();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1", TenantId = "tenant-a" };
        using (var session = _documents!.OpenAsyncSession())
        {
            var outbox = new RavenDbOutbox(runtime, session);
            await session.StoreAsync(new ReferencedAccount { Id = "accounts/1" }, TestContext.Current.CancellationToken);
            await outbox.ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
                new DeliveryOptions { ReferenceKey = "account/1", TenantId = "tenant-a" });
            (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(0);
            // Commit without relay to model a process failure immediately after saving.
            if (commit) await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(commit ? 1 : 0);
        using var verification = _documents!.OpenAsyncSession();
        ((await verification.LoadAsync<ReferencedAccount>("accounts/1", TestContext.Current.CancellationToken)) != null)
            .ShouldBe(commit);
    }

    [Fact]
    public async Task reference_survives_commit_without_relay_another_host_and_restart()
    {
        Guid id;
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1" };
        using (var producer = await startHost())
        {
            using var session = _documents!.OpenAsyncSession();
            var outbox = new RavenDbOutbox(producer.Services.GetRequiredService<IWolverineRuntime>(), session);
            await outbox.ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
                new DeliveryOptions { ReferenceKey = "account/1" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);

            using var observer = await startHost();
            id = (await observer.Services.GetRequiredService<IMessageStore>().ScheduledMessages
                .QueryAsync(query, TestContext.Current.CancellationToken)).Messages.Single().Id;
            await observer.StopAsync(TestContext.Current.CancellationToken);
            await producer.StopAsync(TestContext.Current.CancellationToken);
        }

        using var restarted = await startHost();
        var schedules = restarted.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).Messages.Single().Id.ShouldBe(id);
        await schedules.RescheduleAsync(id, DateTimeOffset.UtcNow.AddMilliseconds(200), TestContext.Current.CancellationToken);
        var observed = await restarted.Services.GetRequiredService<ReferencedWorkObservations>().Started.Task
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        observed.Id.ShouldBe(id);
        observed.ReferenceKey.ShouldBe("account/1");
        (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).TotalCount.ShouldBe(0);
        await restarted.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddHours(2),
            new DeliveryOptions { ReferenceKey = "account/1" });
        (await schedules.QueryAsync(query, TestContext.Current.CancellationToken)).Messages.Single().Id.ShouldNotBe(id);
    }

}

public class ReferencedAccount
{
    public string Id { get; set; } = null!;
}
