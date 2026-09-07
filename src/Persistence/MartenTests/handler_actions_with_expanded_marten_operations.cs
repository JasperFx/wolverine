using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Linq.SoftDeletes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
/// Coverage for the IMartenOp side effects that reach the parts of Marten's
/// IDocumentOperations that MartenOps did not previously expose: hard deletes, soft delete
/// reversal, mixed insert/delete batches, revisioned updates, patching, raw SQL, and appending
/// to or archiving an existing event stream.
/// </summary>
public class handler_actions_with_expanded_marten_operations : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ExpandedMartenOpsHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services
                    .AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "expanded_marten_ops";
                        m.Schema.For<SoftDeletedThing>().SoftDeleted();
                        m.Projections.Snapshot<ThingTotals>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(SoftDeletedThing));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(RevisionedThing));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(PatchableThing));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task hard_delete_by_id_removes_the_row_of_a_soft_deleted_document_type()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new SoftDeletedThing { Id = "hard" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new HardDeleteThing("hard"));

        await using var query = _store.QuerySession();
        var all = await query.Query<SoftDeletedThing>().Where(x => x.MaybeDeleted())
            .ToListAsync(TestContext.Current.CancellationToken);

        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task hard_delete_the_document_itself_removes_the_row()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new SoftDeletedThing { Id = "hard-doc" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new HardDeleteThingDocument("hard-doc"));

        await using var query = _store.QuerySession();
        var all = await query.Query<SoftDeletedThing>().Where(x => x.MaybeDeleted())
            .ToListAsync(TestContext.Current.CancellationToken);

        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task hard_delete_where_removes_the_matching_rows()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new SoftDeletedThing { Id = "hw-1", Number = 1 });
            session.Store(new SoftDeletedThing { Id = "hw-2", Number = 2 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new HardDeleteThingsAbove(1));

        await using var query = _store.QuerySession();
        var remaining = await query.Query<SoftDeletedThing>().Where(x => x.MaybeDeleted())
            .Select(x => x.Id).ToListAsync(TestContext.Current.CancellationToken);

        remaining.ShouldBe(["hw-1"]);
    }

    [Fact]
    public async Task undo_delete_where_brings_a_soft_deleted_document_back()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new SoftDeletedThing { Id = "undo", Number = 7 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);

            session.Delete<SoftDeletedThing>("undo");
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var query = _store.QuerySession())
        {
            (await query.Query<SoftDeletedThing>().Where(x => x.Id == "undo")
                .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

            (await query.Query<SoftDeletedThing>().Where(x => x.IsDeleted())
                .ToListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        }

        await _host.InvokeMessageAndWaitAsync(new UndoDeleteThings(7));

        await using var after = _store.QuerySession();
        (await after.Query<SoftDeletedThing>().Where(x => x.Id == "undo")
            .ToListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task insert_objects_writes_every_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertMixedThings("io-1", 3));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<SoftDeletedThing>("io-1", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await query.LoadAsync<PatchableThing>("io-1", TestContext.Current.CancellationToken))!.Number.ShouldBe(3);
    }

    [Fact]
    public async Task delete_objects_deletes_every_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertMixedThings("do-1", 3));
        await _host.InvokeMessageAndWaitAsync(new DeleteMixedThings("do-1"));

        await using var query = _store.QuerySession();

        // SoftDeletedThing is soft deleted, so DeleteObjects leaves the row behind but filtered
        // out of queries - unlike the HardDelete ops above
        (await query.Query<SoftDeletedThing>().Where(x => x.Id == "do-1")
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await query.Query<SoftDeletedThing>().Where(x => x.MaybeDeleted())
            .ToListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);

        (await query.LoadAsync<PatchableThing>("do-1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task update_revision_advances_the_stored_revision()
    {
        await _host.InvokeMessageAndWaitAsync(new SetThingRevision("rev-1", 5, 1));
        await _host.InvokeMessageAndWaitAsync(new SetThingRevision("rev-1", 9, 2));

        await using var query = _store.QuerySession();
        var doc = await query.LoadAsync<RevisionedThing>("rev-1", TestContext.Current.CancellationToken);

        doc!.Number.ShouldBe(9);
        doc.Version.ShouldBe(2);
    }

    [Fact]
    public async Task update_revision_going_backwards_is_a_concurrency_failure()
    {
        await _host.InvokeMessageAndWaitAsync(new SetThingRevision("rev-2", 5, 3));

        await Should.ThrowAsync<ConcurrencyException>(() =>
            _host.InvokeMessageAndWaitAsync(new SetThingRevision("rev-2", 9, 2)));
    }

    [Fact]
    public async Task try_update_revision_going_backwards_is_silently_ignored()
    {
        await _host.InvokeMessageAndWaitAsync(new SetThingRevision("rev-3", 5, 3));
        await _host.InvokeMessageAndWaitAsync(new TrySetThingRevision("rev-3", 9, 2));

        await using var query = _store.QuerySession();
        var doc = await query.LoadAsync<RevisionedThing>("rev-3", TestContext.Current.CancellationToken);

        doc!.Number.ShouldBe(5);
        doc.Version.ShouldBe(3);
    }

    [Fact]
    public async Task patch_by_id_changes_only_the_patched_property()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PatchableThing { Id = "patch-1", Number = 1, Name = "original" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PatchThingNumber("patch-1", 42));

        await using var query = _store.QuerySession();
        var doc = await query.LoadAsync<PatchableThing>("patch-1", TestContext.Current.CancellationToken);

        doc!.Number.ShouldBe(42);
        doc.Name.ShouldBe("original");
    }

    [Fact]
    public async Task patch_where_changes_every_matching_document()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PatchableThing { Id = "pw-1", Number = 1, Name = "keep" });
            session.Store(new PatchableThing { Id = "pw-2", Number = 2, Name = "keep" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new PatchThingsAbove(1, "patched"));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<PatchableThing>("pw-1", TestContext.Current.CancellationToken))!.Name.ShouldBe("keep");
        (await query.LoadAsync<PatchableThing>("pw-2", TestContext.Current.CancellationToken))!.Name
            .ShouldBe("patched");
    }

    [Fact]
    public async Task queue_sql_command_runs_in_the_same_unit_of_work()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PatchableThing { Id = "sql-1", Number = 1, Name = "sql" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var table = _store.Options.FindOrResolveDocumentType(typeof(PatchableThing)).TableName.QualifiedName;
        await _host.InvokeMessageAndWaitAsync(new DeleteThingWithSql(table, "sql-1"));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<PatchableThing>("sql-1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task append_to_an_existing_stream()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<ThingTotals>(streamId, new ThingIncremented(1));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new IncrementThingStream(streamId, 4));

        await using var query = _store.QuerySession();
        var totals = await query.LoadAsync<ThingTotals>(streamId, TestContext.Current.CancellationToken);

        totals!.Total.ShouldBe(5);
    }

    [Fact]
    public async Task append_to_an_existing_stream_with_a_stale_expected_version()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<ThingTotals>(streamId, new ThingIncremented(1), new ThingIncremented(2));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await Should.ThrowAsync<ConcurrencyException>(() =>
            _host.InvokeMessageAndWaitAsync(new IncrementThingStreamAtVersion(streamId, 4, 1)));
    }

    [Fact]
    public async Task archive_an_existing_stream()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<ThingTotals>(streamId, new ThingIncremented(1));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _host.InvokeMessageAndWaitAsync(new ArchiveThingStream(streamId));

        await using var query = _store.QuerySession();
        var state = await query.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);

        state!.IsArchived.ShouldBeTrue();
    }
}

public record HardDeleteThing(string Id);

public record HardDeleteThingDocument(string Id);

public record HardDeleteThingsAbove(int Number);

public record UndoDeleteThings(int Number);

public record InsertMixedThings(string Id, int Number);

public record DeleteMixedThings(string Id);

public record SetThingRevision(string Id, int Number, int Revision);

public record TrySetThingRevision(string Id, int Number, int Revision);

public record PatchThingNumber(string Id, int Number);

public record PatchThingsAbove(int Number, string Name);

public record DeleteThingWithSql(string TableName, string Id);

public record IncrementThingStream(Guid StreamId, int Amount);

public record IncrementThingStreamAtVersion(Guid StreamId, int Amount, long ExpectedVersion);

public record ArchiveThingStream(Guid StreamId);

public static class ExpandedMartenOpsHandler
{
    public static IMartenOp Handle(HardDeleteThing command)
    {
        return MartenOps.HardDelete<SoftDeletedThing>(command.Id);
    }

    public static async Task<IMartenOp> Handle(HardDeleteThingDocument command, IDocumentSession session)
    {
        var doc = await session.LoadAsync<SoftDeletedThing>(command.Id);
        return MartenOps.HardDelete(doc!);
    }

    public static IMartenOp Handle(HardDeleteThingsAbove command)
    {
        return MartenOps.HardDeleteWhere<SoftDeletedThing>(x => x.Number > command.Number);
    }

    public static IMartenOp Handle(UndoDeleteThings command)
    {
        return MartenOps.UndoDeleteWhere<SoftDeletedThing>(x => x.Number == command.Number);
    }

    public static IMartenOp Handle(InsertMixedThings command)
    {
        return MartenOps.InsertObjects(
            new SoftDeletedThing { Id = command.Id },
            new PatchableThing { Id = command.Id, Number = command.Number });
    }

    public static async Task<IMartenOp> Handle(DeleteMixedThings command, IDocumentSession session)
    {
        var soft = await session.LoadAsync<SoftDeletedThing>(command.Id);
        var patchable = await session.LoadAsync<PatchableThing>(command.Id);

        return MartenOps.DeleteObjects(soft!, patchable!);
    }

    public static IMartenOp Handle(SetThingRevision command)
    {
        return MartenOps.UpdateRevision(
            new RevisionedThing { Id = command.Id, Number = command.Number }, command.Revision);
    }

    public static IMartenOp Handle(TrySetThingRevision command)
    {
        return MartenOps.TryUpdateRevision(
            new RevisionedThing { Id = command.Id, Number = command.Number }, command.Revision);
    }

    public static IMartenOp Handle(PatchThingNumber command)
    {
        return MartenOps.Patch<PatchableThing>(command.Id, x => x.Set(d => d.Number, command.Number));
    }

    public static IMartenOp Handle(PatchThingsAbove command)
    {
        return MartenOps.PatchWhere<PatchableThing>(x => x.Number > command.Number,
            x => x.Set(d => d.Name, command.Name));
    }

    public static IMartenOp Handle(DeleteThingWithSql command)
    {
        return MartenOps.QueueSqlCommand($"delete from {command.TableName} where id = ?", command.Id);
    }

    public static IMartenOp Handle(IncrementThingStream command)
    {
        return MartenOps.Append(command.StreamId, new ThingIncremented(command.Amount));
    }

    public static IMartenOp Handle(IncrementThingStreamAtVersion command)
    {
        return MartenOps.Append(command.StreamId, command.ExpectedVersion, new ThingIncremented(command.Amount));
    }

    public static IMartenOp Handle(ArchiveThingStream command)
    {
        return MartenOps.ArchiveStream(command.StreamId);
    }
}

public class SoftDeletedThing
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
}

public class PatchableThing
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
    public string Name { get; set; } = null!;
}

public class RevisionedThing : IRevisioned
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
    public int Version { get; set; }
}

public record ThingIncremented(int Amount);

public class ThingTotals
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(ThingIncremented e)
    {
        Total += e.Amount;
    }
}
