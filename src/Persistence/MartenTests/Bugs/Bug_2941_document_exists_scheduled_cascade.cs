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
using Wolverine.Marten.Requirements;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// Companion to Bug_aggregate_should_still_publish (GH-2941). The [DocumentExists<T>] /
// [DocumentDoesNotExist<T>] attributes are ModifyChainAttribute-based and inject a
// DocumentExistenceCheckFrame (AsyncFrame, uses IDocumentSession). Like [ReadAggregate], that
// frame's session dependency is invisible to Chain.serviceDependencies (which only walks
// MethodCall middleware) AND runs lazily in applyCustomizations long after AutoApplyTransactions
// has decided CanApply - so a chain decorated only by [DocumentExists<T>] silently lost its
// scheduled cascading messages for the same reason. The CanApply fix adds direct detection of
// these attributes on the handler method / handler type / message type.
public class Bug_2941_document_exists_scheduled_cascade : PostgresqlContext,
    IClassFixture<DocumentExistsScheduledCascadeContext>
{
    private readonly DocumentExistsScheduledCascadeContext _context;

    public Bug_2941_document_exists_scheduled_cascade(DocumentExistsScheduledCascadeContext context)
    {
        _context = context;
    }

    private IHost theHost => _context.Host;

    [Fact]
    public async Task document_exists_handler_schedules_its_cascading_message()
    {
        // The matching document exists (the fixture seeds one), so the [DocumentExists<TestDoc>]
        // guard passes. The handler returns DeliveryMessage<T>.DelayedFor(2s); without the
        // CanApply fix this would time out at 30s because the cascade never lands in
        // wolverine_incoming_envelopes.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<DocExistsScheduled>(theHost)
            .ExecuteAndWaitAsync(_ =>
                theHost.MessageBus().PublishAsync(new ScheduleViaDocExists(DocumentExistsScheduledCascadeContext.SeededId)));

        tracked.Received.MessagesOf<DocExistsScheduled>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task document_does_not_exist_handler_schedules_its_cascading_message()
    {
        // No document with this id exists, so the [DocumentDoesNotExist<TestDoc>] guard passes
        // and the handler runs and schedules. Same CanApply path as DocumentExists.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<DocExistsScheduled>(theHost)
            .ExecuteAndWaitAsync(_ =>
                theHost.MessageBus().PublishAsync(new ScheduleViaDocDoesNotExist(Guid.NewGuid())));

        tracked.Received.MessagesOf<DocExistsScheduled>().Count().ShouldBe(1);
    }
}

public class DocumentExistsScheduledCascadeContext : PostgresqlContext, IAsyncLifetime
{
    private const string Schema = "doc_exists_2941";
    public static readonly Guid SeededId = Guid.NewGuid();

    public IHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(Schema);
            await conn.CloseAsync();
        }

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DocExistsScheduleHandler))
                    .IncludeType(typeof(DocDoesNotExistScheduleHandler))
                    .IncludeType(typeof(DocExistsScheduledSink));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = Schema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Seed the document the DocumentExists handler looks for.
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        session.Store(new TestDoc { Id = SeededId, Name = "seed" });
        await session.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class TestDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record ScheduleViaDocExists(Guid Id);

public record ScheduleViaDocDoesNotExist(Guid Id);

public record DocExistsScheduled(Guid Id);

// [DocumentExists<TestDoc>] is a ModifyChainAttribute, so its frame addition (an AsyncFrame that
// uses IDocumentSession) runs lazily during codegen - long AFTER AutoApplyTransactions has run.
// The GH-2941 CanApply fix detects the attribute directly on the handler method / type so the
// transaction support gets attached anyway.
public static class DocExistsScheduleHandler
{
    [DocumentExists<TestDoc>]
    public static DeliveryMessage<DocExistsScheduled> Handle(ScheduleViaDocExists command)
    {
        return new DocExistsScheduled(command.Id).DelayedFor(TimeSpan.FromSeconds(2));
    }
}

public static class DocDoesNotExistScheduleHandler
{
    [DocumentDoesNotExist<TestDoc>]
    public static DeliveryMessage<DocExistsScheduled> Handle(ScheduleViaDocDoesNotExist command)
    {
        return new DocExistsScheduled(command.Id).DelayedFor(TimeSpan.FromSeconds(2));
    }
}

public static class DocExistsScheduledSink
{
    public static void Handle(DocExistsScheduled message)
    {
    }
}
