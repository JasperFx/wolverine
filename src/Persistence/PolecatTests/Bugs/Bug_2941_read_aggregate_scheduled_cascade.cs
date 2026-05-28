using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.AggregateHandlerWorkflow;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

// Polecat parallel of the Marten regression for GH-2941. A handler that loads an aggregate with
// [ReadAggregate] and returns a SCHEDULED cascading message (DeliveryMessage<T>.DelayedFor(...))
// silently lost the message because PolecatPersistenceFrameProvider.CanApply couldn't see the
// IDocumentSession dependency injected by [ReadAggregate]'s FetchLatestAggregateFrame
// (Chain.serviceDependencies only walks Middleware.OfType<MethodCall>, and the frame is an
// AsyncFrame). Without CanApply returning true, AutoApplyTransactions didn't attach the
// DocumentSessionSaveChanges postprocessor, so the scheduled envelope was queued onto the Polecat
// session via StoreIncoming(...) and never flushed.
//
// The non-scheduled cascade tests are baselines - they exercise the same chain shape but go
// through Wolverine's in-memory local delivery path, which doesn't need the durable inbox.
public class Bug_2941_read_aggregate_scheduled_cascade : IClassFixture<ReadAggregateScheduledCascadeContext>
{
    private readonly ReadAggregateScheduledCascadeContext _context;

    public Bug_2941_read_aggregate_scheduled_cascade(ReadAggregateScheduledCascadeContext context)
    {
        _context = context;
    }

    private IHost theHost => _context.Host;

    [Fact]
    public async Task normal_handler_publishes_its_cascading_message()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcSomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcPublishSomething(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcSomethingWasScheduled>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task normal_handler_schedules_its_cascading_message()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcSomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcScheduleSomething(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcSomethingWasScheduled>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task read_aggregate_handler_publishes_its_cascading_message()
    {
        // Baseline: non-scheduled cascade from a [ReadAggregate] handler. Already worked before
        // the GH-2941 fix because non-scheduled local cascades take Wolverine's in-memory delivery
        // path that doesn't require flushing the session.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcSomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcPublishViaReader(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcSomethingWasScheduled>().Count().ShouldBe(1);
    }

    [Fact(Skip = "Requires Polecat upstream fix: DocumentSessionBase.SaveChangesAsync early-returns when _workTracker has no outstanding work, which silently skips StoreIncomingEnvelopeParticipant added via Session.StoreIncoming(...) for a [ReadAggregate] handler whose body emits only a scheduled cascade (no doc ops, no streams). The Wolverine-side CanApply fix is necessary but not sufficient on Polecat. Unskip when Polecat ships a SaveChangesAsync that runs participants even when no document/stream work is outstanding. GH-2941.")]
    public async Task read_aggregate_handler_schedules_its_cascading_message()
    {
        // The GH-2941 case. Without the CanApply fix this times out: the cascade is recorded as
        // Sent (its StoreIncoming(...) is queued on the Polecat session) but never lands in
        // wolverine_incoming_envelopes because SaveChangesAsync is never called, so the scheduler
        // never picks it up and SomethingWasScheduled is never Received.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcSomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcScheduleViaReader(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcSomethingWasScheduled>().Count().ShouldBe(1);
    }
}

public class ReadAggregateScheduledCascadeContext : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PcReader))
                    .IncludeType(typeof(PcScheduleReader))
                    .IncludeType(typeof(PcSomeOtherHandler));

                opts.Services.AddPolecat(m =>
                    {
                        // Servers.SqlServerConnectionString pins Timeout=5 which is marginal under
                        // emulated SQL Server on Apple Silicon (linux/amd64 image on arm64 host).
                        // Bump locally so init doesn't flake on the docker-compose'd 2025-latest image.
                        m.ConnectionString = Servers.SqlServerConnectionString.Replace("Timeout=5", "Timeout=30");
                        m.DatabaseSchemaName = "polecat_2941";
                        // Polecat 2.0 defaults UseNativeJsonType=true (SQL Server 2025). The repo
                        // docker-compose pins 2022-latest for Apple Silicon; the polecat workflow
                        // overrides to 2025-latest in CI. Stay on string body so the test runs on
                        // either image.
                        m.UseNativeJsonType = false;
                    })
                    .IntegrateWithWolverine(integration =>
                    {
                        integration.MessageStorageSchemaName = "polecat_2941_wol";
                    });

            }).StartAsync();

        // Apply schemas manually rather than via AddResourceSetupOnStartup(ResetState) - the reset
        // path is flaky under emulated SQL Server on Apple Silicon, and this test does not need a
        // pristine schema between runs (the [ReadAggregate(Required = false)] handler tolerates a
        // missing stream).
        _store = Host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public record PcPublishSomething(Guid Id);

public record PcScheduleSomething(Guid Id);

public record PcPublishViaReader(Guid Id);

public record PcScheduleViaReader(Guid Id);

public record PcSomethingWasScheduled(Guid Id);

// [ReadAggregate(Required = false)] - the FetchLatest returns null when the stream does not
// exist; the handler proceeds and emits a non-scheduled cascade. This baseline still works
// without the CanApply fix because local non-scheduled cascades use in-memory delivery.
public static class PcReader
{
    public static PcSomethingWasScheduled Handle(
        PcPublishViaReader command,
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new PcSomethingWasScheduled(command.Id);
    }
}

// Same [ReadAggregate] usage, but the cascade is SCHEDULED. Without the GH-2941 fix the message
// is lost because the chain's Polecat session is never SaveChangesAsync'd.
public static class PcScheduleReader
{
    public static DeliveryMessage<PcSomethingWasScheduled> Handle(
        PcScheduleViaReader command,
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new PcSomethingWasScheduled(command.Id)
            .DelayedFor(TimeSpan.FromSeconds(2));
    }
}

public static class PcSomeOtherHandler
{
    public static PcSomethingWasScheduled Handle(PcPublishSomething command)
    {
        return new PcSomethingWasScheduled(command.Id);
    }

    public static DeliveryMessage<PcSomethingWasScheduled> Handle(PcScheduleSomething command)
    {
        return new PcSomethingWasScheduled(command.Id)
            .DelayedFor(TimeSpan.FromSeconds(2));
    }

    public static void Handle(PcSomethingWasScheduled message)
    {
    }
}
