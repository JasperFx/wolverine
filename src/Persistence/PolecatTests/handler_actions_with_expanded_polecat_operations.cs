using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Attributes;
using Polecat.Linq;
using Polecat.Linq.SoftDeletes;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

// The expanded PolecatOps driven through real handlers against a real Polecat store -- the half the
// op-shape tests cannot reach.
public class handler_actions_with_expanded_polecat_operations : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExpandedPcOpsHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_expanded_ops";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private IDocumentStore theStore => _host.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task hard_delete_removes_the_row_a_soft_delete_would_have_kept()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Store(new PcSoftDeletedDoc { Id = id, Name = "keep" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PcHardDeleteIt(id));

        await using var query = theStore.QuerySession();
        // a soft delete would still return the row to a MaybeDeleted() query; a hard delete does not
        var all = await query.Query<PcSoftDeletedDoc>().MaybeDeleted()
            .ToListAsync(TestContext.Current.CancellationToken);
        all.ShouldNotContain(x => x.Id == id);
    }

    [Fact]
    public async Task undo_delete_where_brings_a_soft_deleted_document_back()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Store(new PcSoftDeletedDoc { Id = id, Name = "revivable" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            session.Delete<PcSoftDeletedDoc>(id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PcUndoTheDelete("revivable"));

        await using var query = theStore.QuerySession();
        (await query.LoadAsync<PcSoftDeletedDoc>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task append_reaches_a_stream_the_handler_does_not_own()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream(id, new ExpandedEventA());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PcAppendToIt(id));

        await using var query = theStore.LightweightSession();
        var events = await query.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task the_archive_lifecycle_round_trips()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream(id, new ExpandedEventA());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PcArchiveIt(id));

        await using (var query = theStore.LightweightSession())
        {
            var state = await query.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
            state!.IsArchived.ShouldBeTrue();
        }

        // the half MartenOps has no counterpart for
        await _host.InvokeMessageAndWaitAsync(new PcUnArchiveIt(id));

        await using var after = theStore.LightweightSession();
        var reopened = await after.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
        reopened!.IsArchived.ShouldBeFalse();
    }
}

// Polecat configures soft delete by attribute, ISoftDeleted, or an all-documents policy -- there is
// no Schema.For<T>().SoftDeleted() the way Marten has
[SoftDeleted]
public class PcSoftDeletedDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record PcHardDeleteIt(Guid Id);

public record PcUndoTheDelete(string Name);

public record PcAppendToIt(Guid Id);

public record PcArchiveIt(Guid Id);

public record PcUnArchiveIt(Guid Id);

// [WolverineIgnore] for the same reason PcInvoiceHandler carries it: these need a registered event
// store at bootstrap, and this is a shared test assembly
[WolverineIgnore]
public static class ExpandedPcOpsHandler
{
    public static HardDeleteDocById<PcSoftDeletedDoc> Handle(PcHardDeleteIt command)
        => PolecatOps.HardDelete<PcSoftDeletedDoc>(command.Id);

    public static UndoDeleteDocWhere<PcSoftDeletedDoc> Handle(PcUndoTheDelete command)
        => PolecatOps.UndoDeleteWhere<PcSoftDeletedDoc>(x => x.Name == command.Name);

    public static AppendToStream Handle(PcAppendToIt command)
        => PolecatOps.Append(command.Id, new ExpandedEventA());

    public static ArchiveStream Handle(PcArchiveIt command)
        => PolecatOps.ArchiveStream(command.Id);

    public static UnArchiveStream Handle(PcUnArchiveIt command)
        => PolecatOps.UnArchiveStream(command.Id);
}
