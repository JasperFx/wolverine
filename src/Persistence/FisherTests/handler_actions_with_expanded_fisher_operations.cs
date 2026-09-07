using Fisher;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

// The expanded FisherOps driven through real handlers against a real Fisher store -- the half the
// op-shape tests cannot reach. Fisher is in-process SQLite, so this costs a temp file rather than a
// container.
public class handler_actions_with_expanded_fisher_operations : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("expanded_fisher_ops");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExpandedOpsHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Schema.For<SoftDeletedDoc>().SoftDeleted();
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    private IDocumentStore theStore => _host.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task hard_delete_removes_the_row_a_soft_delete_would_have_kept()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Store(new SoftDeletedDoc { Id = id, Name = "keep" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new HardDeleteIt(id));

        await using var query = theStore.QuerySession();
        // a soft delete would still return the row to a MaybeDeleted() query; a hard delete does not
        var all = await query.Query<SoftDeletedDoc>().MaybeDeleted()
            .ToListAsync(TestContext.Current.CancellationToken);
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task undo_delete_where_brings_a_soft_deleted_document_back()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Store(new SoftDeletedDoc { Id = id, Name = "revivable" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            session.Delete<SoftDeletedDoc>(id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new UndoTheDelete("revivable"));

        await using var query = theStore.QuerySession();
        (await query.LoadAsync<SoftDeletedDoc>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task patch_reaches_a_single_document_by_id()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Store(new PatchableDoc { Id = id, Name = "before" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PatchIt(id, "after"));

        await using var query = theStore.QuerySession();
        var loaded = await query.LoadAsync<PatchableDoc>(id, TestContext.Current.CancellationToken);
        loaded!.Name.ShouldBe("after");
    }

    [Fact]
    public async Task try_update_revision_writes_forward_and_drops_a_stale_revision()
    {
        var id = Guid.NewGuid();

        // moving the revision forward writes
        await _host.InvokeMessageAndWaitAsync(new TryReviseIt(id, "v7", 7));

        await using (var query = theStore.QuerySession())
        {
            var moved = await query.LoadAsync<RevisionedDoc>(id, TestContext.Current.CancellationToken);
            moved!.Name.ShouldBe("v7");
        }

        // and a revision the store has already passed is dropped rather than failing the unit of
        // work, which is the whole distinction from UpdateRevision
        await _host.InvokeMessageAndWaitAsync(new TryReviseIt(id, "stale", 3));

        await using var after = theStore.QuerySession();
        var loaded = await after.LoadAsync<RevisionedDoc>(id, TestContext.Current.CancellationToken);
        loaded!.Name.ShouldBe("v7");
    }

    [Fact]
    public async Task update_revision_fails_the_unit_of_work_on_a_stale_revision()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new ReviseIt(id, "v7", 7));

        // the loud half of the same guard: a stale UpdateRevision aborts rather than dropping
        await Should.ThrowAsync<Exception>(async () =>
            await _host.InvokeMessageAndWaitAsync(new ReviseIt(id, "stale", 3)));

        await using var after = theStore.QuerySession();
        var loaded = await after.LoadAsync<RevisionedDoc>(id, TestContext.Current.CancellationToken);
        loaded!.Name.ShouldBe("v7");
    }

    [Fact]
    public async Task append_and_archive_reach_a_stream_the_handler_does_not_own()
    {
        var id = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            session.Events.StartStream(id, new ExpandedEventA());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new AppendToIt(id));

        await using (var query = theStore.LightweightSession())
        {
            var events = await query.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
            events.Count.ShouldBe(2);
        }

        await _host.InvokeMessageAndWaitAsync(new ArchiveIt(id));

        await using var after = theStore.LightweightSession();
        var state = await after.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
        state!.IsArchived.ShouldBeTrue();
    }
}

public class SoftDeletedDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class PatchableDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

// IRevisioned rather than Schema.For<T>().UseNumericRevisions(): the two are documented as
// equivalent routes to numeric revisions, but only the interface is actually enforced today --
// a UseNumericRevisions() type takes a stale write silently. See JasperFx/fisher#228.
public class RevisionedDoc : IRevisioned
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public int Version { get; set; }
}

public record HardDeleteIt(Guid Id);

public record UndoTheDelete(string Name);

public record PatchIt(Guid Id, string Name);

public record TryReviseIt(Guid Id, string Name, int Revision);

public record ReviseIt(Guid Id, string Name, int Revision);

public record AppendToIt(Guid Id);

public record ArchiveIt(Guid Id);

// [WolverineIgnore] for the same reason FiInvoiceHandler carries it: these need a registered event
// store at bootstrap, and this is a shared test assembly
[WolverineIgnore]
public static class ExpandedOpsHandler
{
    public static HardDeleteDocById<SoftDeletedDoc> Handle(HardDeleteIt command)
        => FisherOps.HardDelete<SoftDeletedDoc>(command.Id);

    public static UndoDeleteDocWhere<SoftDeletedDoc> Handle(UndoTheDelete command)
        => FisherOps.UndoDeleteWhere<SoftDeletedDoc>(x => x.Name == command.Name);

    public static PatchDoc<PatchableDoc> Handle(PatchIt command)
        => FisherOps.Patch<PatchableDoc>(command.Id, p => p.Set(x => x.Name, command.Name));

    public static TryUpdateDocRevision<RevisionedDoc> Handle(TryReviseIt command)
        => FisherOps.TryUpdateRevision(new RevisionedDoc { Id = command.Id, Name = command.Name }, command.Revision);

    public static UpdateDocRevision<RevisionedDoc> Handle(ReviseIt command)
        => FisherOps.UpdateRevision(new RevisionedDoc { Id = command.Id, Name = command.Name }, command.Revision);

    public static AppendToStream Handle(AppendToIt command)
        => FisherOps.Append(command.Id, new ExpandedEventA());

    public static ArchiveStream Handle(ArchiveIt command)
        => FisherOps.ArchiveStream(command.Id);
}
