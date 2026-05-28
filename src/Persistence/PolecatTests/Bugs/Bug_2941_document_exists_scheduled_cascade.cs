using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Polecat.Requirements;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

// Polecat parallel of MartenTests.Bugs.Bug_2941_document_exists_scheduled_cascade. The
// [DocumentExists<T>] / [DocumentDoesNotExist<T>] attributes here are Polecat's own (mirror of
// Marten's), and they have the same GH-2941 root cause: the DocumentExistenceCheckFrame
// (AsyncFrame, uses IDocumentSession) is invisible to Chain.serviceDependencies AND runs lazily
// in applyCustomizations after AutoApplyTransactions has already evaluated CanApply. The
// PolecatPersistenceFrameProvider.CanApply fix adds direct attribute detection so the chain gets
// SaveChangesAsync postprocessing and scheduled cascades aren't lost.
public class Bug_2941_document_exists_scheduled_cascade
    : IClassFixture<PolecatDocumentExistsScheduledCascadeContext>
{
    private readonly PolecatDocumentExistsScheduledCascadeContext _context;

    public Bug_2941_document_exists_scheduled_cascade(PolecatDocumentExistsScheduledCascadeContext context)
    {
        _context = context;
    }

    private IHost theHost => _context.Host;

    [Fact(Skip = "Requires Polecat upstream fix: see Bug_2941_read_aggregate_scheduled_cascade for details. Same SaveChangesAsync-skips-participants root cause.")]
    public async Task document_exists_handler_schedules_its_cascading_message()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcDocExistsScheduled>(theHost)
            .ExecuteAndWaitAsync(_ =>
                theHost.MessageBus().PublishAsync(new PcScheduleViaDocExists(PolecatDocumentExistsScheduledCascadeContext.SeededId)));

        tracked.Received.MessagesOf<PcDocExistsScheduled>().Count().ShouldBe(1);
    }

    [Fact(Skip = "Requires Polecat upstream fix: see Bug_2941_read_aggregate_scheduled_cascade for details.")]
    public async Task document_does_not_exist_handler_schedules_its_cascading_message()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcDocExistsScheduled>(theHost)
            .ExecuteAndWaitAsync(_ =>
                theHost.MessageBus().PublishAsync(new PcScheduleViaDocDoesNotExist(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcDocExistsScheduled>().Count().ShouldBe(1);
    }
}

public class PolecatDocumentExistsScheduledCascadeContext : IAsyncLifetime
{
    public static readonly Guid SeededId = Guid.NewGuid();

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
                    .IncludeType(typeof(PcDocExistsScheduleHandler))
                    .IncludeType(typeof(PcDocDoesNotExistScheduleHandler))
                    .IncludeType(typeof(PcDocExistsScheduledSink));

                opts.Services.AddPolecat(m =>
                    {
                        // See Bug_2941_read_aggregate_scheduled_cascade for why Timeout=5 is bumped.
                        m.ConnectionString = Servers.SqlServerConnectionString.Replace("Timeout=5", "Timeout=30");
                        m.DatabaseSchemaName = "doc_exists_2941";
                        m.UseNativeJsonType = false;
                    })
                    .IntegrateWithWolverine(integration =>
                    {
                        integration.MessageStorageSchemaName = "doc_exists_2941_wol";
                    });
            }).StartAsync();

        _store = Host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        // Seed the document the DocumentExists handler looks for.
        await using var session = _store.LightweightSession();
        session.Store(new PcTestDoc { Id = SeededId, Name = "seed" });
        await session.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class PcTestDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record PcScheduleViaDocExists(Guid Id);

public record PcScheduleViaDocDoesNotExist(Guid Id);

public record PcDocExistsScheduled(Guid Id);

public static class PcDocExistsScheduleHandler
{
    [DocumentExists<PcTestDoc>]
    public static DeliveryMessage<PcDocExistsScheduled> Handle(PcScheduleViaDocExists command)
    {
        return new PcDocExistsScheduled(command.Id).DelayedFor(TimeSpan.FromSeconds(2));
    }
}

public static class PcDocDoesNotExistScheduleHandler
{
    [DocumentDoesNotExist<PcTestDoc>]
    public static DeliveryMessage<PcDocExistsScheduled> Handle(PcScheduleViaDocDoesNotExist command)
    {
        return new PcDocExistsScheduled(command.Id).DelayedFor(TimeSpan.FromSeconds(2));
    }
}

public static class PcDocExistsScheduledSink
{
    public static void Handle(PcDocExistsScheduled message)
    {
    }
}
