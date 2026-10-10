using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Xunit;

namespace Wolverine.ComplianceTests.Scheduling;

public abstract class ScheduledMessageReferenceCompliance
{
    protected abstract void configurePersistence(WolverineOptions options);

    protected Task<IHost> startHost(string serviceName = "schedule-references", TimeSpan? firstPoll = null)
    {
        return Host.CreateDefaultBuilder().UseWolverine(options =>
        {
            options.ServiceName = serviceName;
            options.Services.AddSingleton<ReferencedWorkObservations>();
            options.Durability.Mode = DurabilityMode.Solo;
            options.Durability.ScheduledJobFirstExecution = firstPoll ?? TimeSpan.FromMilliseconds(100);
            options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
            options.Discovery.DisableConventionalDiscovery().IncludeType<ReferencedWorkHandler>();
            options.PublishMessage<ReferencedWork>().ToLocalQueue("references").UseDurableInbox();
            options.PublishMessage<OtherReferencedWork>().ToLocalQueue("other-references").UseDurableInbox();
            configurePersistence(options);
        }).StartAsync();
    }

    [Fact]
    public async Task finds_related_work_across_message_types_and_destinations()
    {
        using var host = await startHost();
        var due = DateTimeOffset.UtcNow.AddHours(2);
        var bus = host.MessageBus();
        await bus.ScheduleAsync(new ReferencedWork(1), due,
            new DeliveryOptions { ReferenceKey = "account/1" });
        await bus.ScheduleAsync(new OtherReferencedWork(1), due.AddMinutes(1),
            new DeliveryOptions { ReferenceKey = "account/1" });
        await bus.ScheduleAsync(new ReferencedWork(2), due,
            new DeliveryOptions { ReferenceKey = "account/2" });
        await bus.ScheduleAsync(new ReferencedWork(1), due);

        var results = await host.Services.GetRequiredService<IMessageStore>().ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { ReferenceKey = "account/1" }, CancellationToken.None);
        results.TotalCount.ShouldBe(2);
        results.Messages.Select(x => x.ReferenceKey).ShouldAllBe(x => x == "account/1");
        results.Messages.Select(x => x.Id).Distinct().Count().ShouldBe(2);
        results.Messages.Select(x => x.Destination).Distinct().Count().ShouldBe(2);
        results.Messages.Select(x => x.ScheduledTime).ShouldBe(new DateTimeOffset?[] { due, due.AddMinutes(1) });
    }
    [Fact]
    public async Task cancellation_combines_ids_reference_tenant_type_and_time_and_ignores_pagination()
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var due = DateTimeOffset.UtcNow.AddHours(2);
        foreach (var reference in new[] { "account/1", "account/2" })
        foreach (var tenant in new[] { "Tenant-A", "Tenant-B" })
        foreach (var time in new[] { due, due.AddHours(1) })
        {
            var options = new DeliveryOptions { ReferenceKey = reference, TenantId = tenant };
            await host.MessageBus().ScheduleAsync(new ReferencedWork(1), time, options);
            await host.MessageBus().ScheduleAsync(new OtherReferencedWork(1), time, options);
        }

        var all = await schedules.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None);
        var query = new ScheduledMessageQuery
        {
            ReferenceKey = "account/1", TenantId = "Tenant-A",
            MessageIds = all.Messages.Select(x => x.Id).ToArray(),
            MessageType = all.Messages.First(x => x.Destination == "local://references/").MessageType,
            ExecutionTimeFrom = due, ExecutionTimeTo = due,
            PageNumber = 99, PageSize = 1
        };
        await schedules.CancelAsync(query, CancellationToken.None);
        var remaining = await schedules.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None);
        remaining.TotalCount.ShouldBe(15);
        query.PageNumber = 1;
        (await schedules.QueryAsync(query, CancellationToken.None)).TotalCount.ShouldBe(0);

        // A second reference-wide cancellation must include all matching pages and types.
        await schedules.CancelAsync(new ScheduledMessageQuery
        {
            ReferenceKey = "account/1", TenantId = "Tenant-B", PageSize = 1
        }, CancellationToken.None);
        (await schedules.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None)).TotalCount.ShouldBe(11);
    }

    [Fact]
    public async Task reference_and_tenant_filters_are_exact_and_reference_queries_are_application_scoped()
    {
        using var host = await startHost();
        using var other = await startHost("another-application");
        var due = DateTimeOffset.UtcNow.AddHours(2);
        foreach (var producer in new[] { host, other })
        foreach (var tenant in new[] { "Tenant-A", "tenant-a", null })
        {
            await producer.MessageBus().ScheduleAsync(new ReferencedWork(1), due,
                new DeliveryOptions { ReferenceKey = "Account/1", TenantId = tenant });
        }

        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "account/1" }, CancellationToken.None))
            .TotalCount.ShouldBe(0);
        (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "Account/1" }, CancellationToken.None))
            .TotalCount.ShouldBe(3);
        var exact = new ScheduledMessageQuery { ReferenceKey = "Account/1", TenantId = "Tenant-A" };
        (await schedules.QueryAsync(exact, CancellationToken.None)).Messages.Single().TenantId.ShouldBe("Tenant-A");
        await schedules.CancelAsync(exact, CancellationToken.None);
        (await schedules.QueryAsync(new ScheduledMessageQuery { ReferenceKey = "Account/1" }, CancellationToken.None))
            .TotalCount.ShouldBe(2);
        (await other.Services.GetRequiredService<IMessageStore>().ScheduledMessages.QueryAsync(exact, CancellationToken.None))
            .TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task rescheduling_keeps_reference_and_payload_and_queries_use_current_due_time()
    {
        using var host = await startHost();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var schedules = store.ScheduledMessages;
        var due = DateTimeOffset.UtcNow.AddHours(2);
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), due,
            new DeliveryOptions { ReferenceKey = "account/1" });
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1" };
        var original = (await schedules.QueryAsync(query, CancellationToken.None)).Messages.Single();
        var payload = (await store.Admin.AllIncomingAsync()).Single().Data;
        foreach (var next in new[] { due.AddHours(1), due.AddHours(-1), due.AddHours(2) })
        {
            await schedules.RescheduleAsync(original.Id, next, CancellationToken.None);
            var current = (await schedules.QueryAsync(query, CancellationToken.None)).Messages.Single();
            current.Id.ShouldBe(original.Id);
            current.ScheduledTime.ShouldBe(next);
            current.ReferenceKey.ShouldBe("account/1");
            (await store.Admin.AllIncomingAsync()).Single().Data.ShouldBe(payload);
        }

        // A scheduled retry keeps the association and is visible at its new due time.
        var retry = (await store.Admin.AllIncomingAsync()).Single();
        retry.ScheduledTime = due.AddHours(3);
        await store.Inbox.ScheduleExecutionAsync(retry);
        query.ExecutionTimeFrom = retry.ScheduledTime;
        query.ExecutionTimeTo = retry.ScheduledTime;
        (await schedules.QueryAsync(query, CancellationToken.None)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task concurrent_schedules_keep_independent_ids_and_reference_can_be_reused()
    {
        using var host = await startHost();
        var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
        var due = DateTimeOffset.UtcNow.AddHours(2);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
            await host.MessageBus().ScheduleAsync(new ReferencedWork(1), due.AddMinutes(i),
                new DeliveryOptions { ReferenceKey = "account/1" })));
        var query = new ScheduledMessageQuery { ReferenceKey = "account/1", PageSize = 2, PageNumber = 2 };
        var page = await schedules.QueryAsync(query, CancellationToken.None);
        page.TotalCount.ShouldBe(5);
        page.Messages.Count.ShouldBe(2);
        page.Messages.Select(x => x.ScheduledTime).ShouldBe(new DateTimeOffset?[] { due.AddMinutes(2), due.AddMinutes(3) });
        await schedules.CancelAsync(query, CancellationToken.None);
        (await schedules.QueryAsync(query, CancellationToken.None)).TotalCount.ShouldBe(0);
        await host.MessageBus().ScheduleAsync(new ReferencedWork(1), due,
            new DeliveryOptions { ReferenceKey = "account/1" });
        query.PageNumber = 1;
        var recreated = (await schedules.QueryAsync(query, CancellationToken.None)).Messages.Single();
        page.Messages.ShouldNotContain(x => x.Id == recreated.Id);
    }

    [Fact]
    public async Task running_work_is_excluded_and_cannot_be_cancelled_by_reference()
    {
        using var host = await startHost();
        var observations = host.Services.GetRequiredService<ReferencedWorkObservations>();
        observations.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await host.MessageBus().ScheduleAsync(new ReferencedWork(1), DateTimeOffset.UtcNow.AddMilliseconds(200),
                new DeliveryOptions { ReferenceKey = "account/1" });
            var envelope = await observations.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            envelope.ReferenceKey.ShouldBe("account/1");
            var schedules = host.Services.GetRequiredService<IMessageStore>().ScheduledMessages;
            var query = new ScheduledMessageQuery { ReferenceKey = "account/1" };
            (await schedules.QueryAsync(query, CancellationToken.None)).TotalCount.ShouldBe(0);
            await schedules.CancelAsync(query, CancellationToken.None);
            (await host.Services.GetRequiredService<IMessageStore>().Admin.AllIncomingAsync())
                .ShouldContain(x => x.Id == envelope.Id);
        }
        finally
        {
            observations.Release.TrySetResult();
        }
    }

}

public record ReferencedWork(int AccountId);
public record OtherReferencedWork(int AccountId);

public class ReferencedWorkHandler
{
    public static async Task Handle(ReferencedWork message, Envelope envelope, ReferencedWorkObservations observations)
    {
        observations.Received.Enqueue(envelope);
        observations.Started.TrySetResult(envelope);
        if (message.AccountId == 2) observations.Sentinel.TrySetResult();
        if (observations.Release != null) await observations.Release.Task;
    }
    public static void Handle(OtherReferencedWork message) { }
}

public class ReferencedWorkObservations
{
    public System.Collections.Concurrent.ConcurrentQueue<Envelope> Received { get; } = new();
    public TaskCompletionSource<Envelope> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Sentinel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Release { get; set; }
}
