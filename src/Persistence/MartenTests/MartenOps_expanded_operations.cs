using Shouldly;
using Wolverine.Marten;

namespace MartenTests;

public record ExpandedOpsDoc(string Id, int Number);

public record ExpandedOpsEvent(string Name);

public class MartenOps_expanded_operations
{
    [Fact]
    public void hard_delete_by_id_rejects_an_id_type_Marten_cannot_dispatch()
    {
        // Marten's HardDelete<T>() has no object-typed overload, so a strong-typed id has to be
        // refused up front rather than at SaveChangesAsync() time
        Should.Throw<ArgumentOutOfRangeException>(() => new HardDeleteDocById<ExpandedOpsDoc>(1.5m));
        Should.Throw<ArgumentNullException>(() => new HardDeleteDocById<ExpandedOpsDoc>(null!));
    }

    [Fact]
    public void patch_by_id_rejects_an_id_type_Marten_cannot_dispatch()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PatchDoc<ExpandedOpsDoc>(1.5m, x => x.Set(d => d.Number, 5)));
    }

    [Fact]
    public void insert_and_delete_objects_track_their_documents()
    {
        var insert = MartenOps.InsertObjects(new ExpandedOpsDoc("one", 1))
            .With(new ExpandedOpsDoc("two", 2))
            .With([new ExpandedOpsDoc("three", 3), new ExpandedOpsDoc("four", 4)]);

        insert.Documents.Count.ShouldBe(4);

        var delete = MartenOps.DeleteObjects(new ExpandedOpsDoc("one", 1))
            .With(new ExpandedOpsDoc("two", 2));

        delete.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void revision_ops_carry_the_expected_revision()
    {
        MartenOps.UpdateRevision(new ExpandedOpsDoc("one", 1), 5).Revision.ShouldBe(5);
        MartenOps.TryUpdateRevision(new ExpandedOpsDoc("one", 1), 5).Revision.ShouldBe(5);

        var version = Guid.NewGuid();
        MartenOps.UpdateExpectedVersion(new ExpandedOpsDoc("one", 1), version).Version.ShouldBe(version);
    }

    [Fact]
    public void queue_sql_command_captures_the_sql_and_parameters()
    {
        var op = MartenOps.QueueSqlCommand("delete from mt_doc_target where id = ?", 5);

        op.Sql.ShouldBe("delete from mt_doc_target where id = ?");
        op.ParameterValues.ShouldBe([5]);
        op.Placeholder.ShouldBeNull();

        MartenOps.QueueSqlCommand('^', "delete from mt_doc_target where id = ^", 5)
            .Placeholder.ShouldBe('^');
    }

    [Fact]
    public void append_captures_the_stream_identity_and_expected_version()
    {
        var streamId = Guid.NewGuid();

        var byId = MartenOps.Append(streamId, new ExpandedOpsEvent("one"));
        byId.StreamId.ShouldBe(streamId);
        byId.StreamKey.ShouldBe(string.Empty);
        byId.ExpectedVersion.ShouldBeNull();
        byId.Events.Count.ShouldBe(1);

        var byKey = MartenOps.Append("blue", 3, new ExpandedOpsEvent("one"))
            .With(new ExpandedOpsEvent("two"));
        byKey.StreamKey.ShouldBe("blue");
        byKey.StreamId.ShouldBe(Guid.Empty);
        byKey.ExpectedVersion.ShouldBe(3);
        byKey.Events.Count.ShouldBe(2);
    }

    [Fact]
    public void append_and_archive_refuse_an_empty_stream_identity()
    {
        // Both ops discriminate Guid identity from string identity on StreamId == Guid.Empty,
        // so an empty identity would silently take the wrong branch
        Should.Throw<ArgumentOutOfRangeException>(() => MartenOps.Append(Guid.Empty, new ExpandedOpsEvent("one")));
        Should.Throw<ArgumentOutOfRangeException>(() => MartenOps.Append("", new ExpandedOpsEvent("one")));
        Should.Throw<ArgumentOutOfRangeException>(() => MartenOps.ArchiveStream(Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => MartenOps.ArchiveStream(""));
    }

    [Fact]
    public void archive_stream_captures_the_stream_identity()
    {
        var streamId = Guid.NewGuid();

        MartenOps.ArchiveStream(streamId).StreamId.ShouldBe(streamId);
        MartenOps.ArchiveStream("blue").StreamKey.ShouldBe("blue");
    }

    [Fact]
    public void for_tenant_scopes_any_op_without_losing_its_concrete_type()
    {
        // The generic ForTenant() is the single tenant mechanism for every op, so a new op does
        // not have to grow a parallel set of tenantId overloads to be tenant-aware
        MartenOps.Store(new ExpandedOpsDoc("one", 1)).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.HardDelete<ExpandedOpsDoc>("one").ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.HardDeleteWhere<ExpandedOpsDoc>(x => x.Number > 1).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.UndoDeleteWhere<ExpandedOpsDoc>(x => x.Number > 1).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.InsertObjects(new ExpandedOpsDoc("one", 1)).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.DeleteObjects(new ExpandedOpsDoc("one", 1)).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.UpdateRevision(new ExpandedOpsDoc("one", 1), 2).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.Patch<ExpandedOpsDoc>("one", x => x.Set(d => d.Number, 5)).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.QueueSqlCommand("select 1").ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.Append(Guid.NewGuid(), new ExpandedOpsEvent("one")).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.ArchiveStream(Guid.NewGuid()).ForTenant("blue").TenantId.ShouldBe("blue");
        MartenOps.StartStream<ExpandedOpsDoc>(Guid.NewGuid(), new ExpandedOpsEvent("one"))
            .ForTenant("blue").TenantId.ShouldBe("blue");
    }

    [Fact]
    public void for_tenant_will_not_take_a_null_tenant_id()
    {
        Should.Throw<ArgumentNullException>(() => MartenOps.Store(new ExpandedOpsDoc("one", 1)).ForTenant(null!));
    }
}
