using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
// JasperFx 2.0 lifted DocumentAlreadyExistsException into the JasperFx namespace.
using DocumentAlreadyExistsException = JasperFx.DocumentAlreadyExistsException;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

// Polecat counterpart of the Wolverine.Marten regression: when a durable-local-queue
// handler enlists the Polecat outbox and SaveChangesAsync rolls back (duplicate document
// insert), a .Discard() policy must still clear the incoming envelope. The bug was that
// FlushOutgoingMessagesOnCommit flipped Envelope.Status to Handled before the commit; on
// rollback the DB row stayed 'Incoming' but the stale in-memory flag made DurableReceiver's
// _markAsHandled optimization skip the real UPDATE, stranding the row as 'Incoming'.
public class Bug_discard_after_failed_outbox_commit : IAsyncLifetime
{
    private const string Schema = "discard_failed_commit_pc";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DoPcThingHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = Schema;
                    })
                    .IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task discarded_duplicate_should_not_be_stuck_in_incoming()
    {
        var store = _host.Services.GetRequiredService<IMessageStore>();
        await store.Admin.ClearAllAsync();

        var id = Guid.NewGuid().ToString();

        // 1st send: inserts PcMarker(id), commits, envelope marked Handled.
        await _host.TrackActivity().Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.PublishAsync(new DoPcThing(id)));

        // 2nd send (duplicate): PolecatOps.Insert hits the unique PK inside the outbox
        // SaveChangesAsync => DocumentAlreadyExistsException => .Discard().
        await _host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.PublishAsync(new DoPcThing(id)));

        var counts = await store.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
    }
}

public record DoPcThing(string Id);

// Polecat document; `Id` is the identity => unique PK. A 2nd Insert of the same Id throws.
public record PcMarker(string Id);

public static class DoPcThingHandler
{
    public static void Configure(HandlerChain chain)
        => chain.OnException<DocumentAlreadyExistsException>().Discard();

    public static IPolecatOp Handle(DoPcThing message)
        => PolecatOps.Insert(new PcMarker(message.Id));
}
