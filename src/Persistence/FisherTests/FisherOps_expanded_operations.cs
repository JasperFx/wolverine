using Shouldly;
using Wolverine.Fisher;

namespace FisherTests;

public record ExpandedDoc(Guid Id, string Name, int Revision = 0);

// The Fisher half of the MartenOps parity wave (GH-4384's "Not in this PR"). These are op-shape
// tests -- what the factory builds, what it refuses, and how ForTenant() rides along -- with the
// behaviour against a real store covered in handler_actions_with_expanded_fisher_operations.
public class FisherOps_expanded_operations
{
    [Fact]
    public void hard_delete_by_document_and_by_id()
    {
        FisherOps.HardDelete(new ExpandedDoc(Guid.NewGuid(), "one")).ShouldBeOfType<HardDeleteDoc<ExpandedDoc>>();

        FisherOps.HardDelete<ExpandedDoc>(Guid.NewGuid()).ShouldBeOfType<HardDeleteDocById<ExpandedDoc>>();
        FisherOps.HardDelete<ExpandedDoc>("key").ShouldBeOfType<HardDeleteDocById<ExpandedDoc>>();
        FisherOps.HardDelete<ExpandedDoc>(5).ShouldBeOfType<HardDeleteDocById<ExpandedDoc>>();
        FisherOps.HardDelete<ExpandedDoc>(5L).ShouldBeOfType<HardDeleteDocById<ExpandedDoc>>();
    }

    [Fact]
    public void hard_delete_refuses_an_id_type_fisher_cannot_dispatch()
    {
        // Fisher's HardDelete<T>() has no object-typed overload to fall back on, so an id type it
        // cannot dispatch would otherwise surface inside SaveChangesAsync(), long after the handler
        // returned a side effect that looked perfectly valid
        Should.Throw<ArgumentOutOfRangeException>(() => new HardDeleteDocById<ExpandedDoc>(1.5m));
        Should.Throw<ArgumentNullException>(() => new HardDeleteDocById<ExpandedDoc>(null!));
    }

    [Fact]
    public void patch_refuses_an_id_type_fisher_cannot_dispatch()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PatchDoc<ExpandedDoc>(1.5m, _ => { }));
        Should.Throw<ArgumentNullException>(() =>
            new PatchDoc<ExpandedDoc>(Guid.NewGuid(), null!));
    }

    [Fact]
    public void revision_ops_carry_the_revision()
    {
        var doc = new ExpandedDoc(Guid.NewGuid(), "one");

        FisherOps.UpdateRevision(doc, 3).Revision.ShouldBe(3);
        FisherOps.TryUpdateRevision(doc, 4).Revision.ShouldBe(4);

        Should.Throw<ArgumentNullException>(() => FisherOps.UpdateRevision<ExpandedDoc>(null!, 1));
        Should.Throw<ArgumentNullException>(() => FisherOps.TryUpdateRevision<ExpandedDoc>(null!, 1));
    }

    [Fact]
    public void queue_sql_command_carries_the_sql_and_the_optional_placeholder()
    {
        var op = FisherOps.QueueSqlCommand("delete from foo where id = ?", 5);
        op.Sql.ShouldBe("delete from foo where id = ?");
        op.ParameterValues.ShouldBe(new object[] { 5 });
        op.Placeholder.ShouldBeNull();

        FisherOps.QueueSqlCommand('^', "select ? from bar", 1).Placeholder.ShouldBe('^');

        Should.Throw<ArgumentNullException>(() => FisherOps.QueueSqlCommand(null!));
    }

    [Fact]
    public void append_carries_its_events_and_optional_expected_version()
    {
        var id = Guid.NewGuid();

        var op = FisherOps.Append(id, new ExpandedEventA());
        op.StreamId.ShouldBe(id);
        op.Events.Count.ShouldBe(1);
        op.ExpectedVersion.ShouldBeNull();

        op.With(new ExpandedEventA()).Events.Count.ShouldBe(2);
        op.With([new ExpandedEventA(), new ExpandedEventA()]).Events.Count.ShouldBe(4);

        FisherOps.Append(id, 3L, new ExpandedEventA()).ExpectedVersion.ShouldBe(3);
        FisherOps.Append("key", 3L, new ExpandedEventA()).ExpectedVersion.ShouldBe(3);
        FisherOps.Append("key", new ExpandedEventA()).StreamKey.ShouldBe("key");
    }

    [Fact]
    public void the_stream_ops_refuse_an_empty_identity()
    {
        // Guid identity is discriminated from string identity on StreamId == Guid.Empty -- the same
        // sentinel StartStream<T> uses -- so an empty identity would silently take the string branch
        Should.Throw<ArgumentOutOfRangeException>(() => FisherOps.Append(Guid.Empty, new ExpandedEventA()));
        Should.Throw<ArgumentOutOfRangeException>(() => FisherOps.Append("", new ExpandedEventA()));
        Should.Throw<ArgumentOutOfRangeException>(() => FisherOps.ArchiveStream(Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => FisherOps.ArchiveStream(""));
    }

    [Fact]
    public void for_tenant_scopes_every_op_and_keeps_the_concrete_return_type()
    {
        var doc = new ExpandedDoc(Guid.NewGuid(), "one");
        var id = Guid.NewGuid();

        FisherOps.HardDelete(doc).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.HardDelete<ExpandedDoc>(id).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.HardDeleteWhere<ExpandedDoc>(x => x.Name == "one").ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.UndoDeleteWhere<ExpandedDoc>(x => x.Name == "one").ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.UpdateRevision(doc, 2).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.TryUpdateRevision(doc, 2).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.Patch<ExpandedDoc>(id, _ => { }).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.PatchWhere<ExpandedDoc>(x => x.Name == "one", _ => { }).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.QueueSqlCommand("select 1").ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.ArchiveStream(id).ForTenant("blue").TenantId.ShouldBe("blue");

        // the concrete type survives, so this chains onto the ops that have a fluent surface
        FisherOps.Append(id, new ExpandedEventA()).ForTenant("blue").With(new ExpandedEventA())
            .Events.Count.ShouldBe(2);

        // and the pre-existing ops joined the same mechanism rather than growing more overloads
        FisherOps.Store(doc).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.StoreMany(doc).ForTenant("blue").TenantId.ShouldBe("blue");
        FisherOps.Delete<ExpandedDoc>(id).ForTenant("blue").TenantId.ShouldBe("blue");

        Should.Throw<ArgumentNullException>(() => FisherOps.Store(doc).ForTenant(null!));
    }
}

public record ExpandedEventA;
