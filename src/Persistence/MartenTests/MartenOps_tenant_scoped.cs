using Shouldly;
using Wolverine.Marten;

namespace MartenTests;

public record TenantTestDoc(Guid Id, string Name);
public record TenantTestDoc2(Guid Id, string Label);

public class MartenOps_tenant_scoped
{
    private const string TestTenantId = "tenant-1";

    [Fact]
    public void store_with_tenant_id()
    {
        var doc = new TenantTestDoc(Guid.NewGuid(), "Widget");
        var op = MartenOps.Store(doc, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
        op.Document.ShouldBe(doc);
    }

    [Fact]
    public void store_without_tenant_id_has_null_tenant()
    {
        var doc = new TenantTestDoc(Guid.NewGuid(), "Widget");
        var op = MartenOps.Store(doc);

        op.TenantId.ShouldBeNull();
    }

    [Fact]
    public void store_many_with_tenant_id()
    {
        var docs = new[] { new TenantTestDoc(Guid.NewGuid(), "A"), new TenantTestDoc(Guid.NewGuid(), "B") };
        var op = MartenOps.StoreMany(TestTenantId, docs);

        op.TenantId.ShouldBe(TestTenantId);
        op.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void store_objects_with_tenant_id()
    {
        var op = MartenOps.StoreObjects(TestTenantId, new TenantTestDoc(Guid.NewGuid(), "A"), new TenantTestDoc2(Guid.NewGuid(), "B"));

        op.TenantId.ShouldBe(TestTenantId);
        op.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void insert_with_tenant_id()
    {
        var doc = new TenantTestDoc(Guid.NewGuid(), "Widget");
        var op = MartenOps.Insert(doc, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
        op.Document.ShouldBe(doc);
    }

    [Fact]
    public void update_with_tenant_id()
    {
        var doc = new TenantTestDoc(Guid.NewGuid(), "Widget");
        var op = MartenOps.Update(doc, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
        op.Document.ShouldBe(doc);
    }

    [Fact]
    public void delete_document_with_tenant_id()
    {
        var doc = new TenantTestDoc(Guid.NewGuid(), "Widget");
        var op = MartenOps.Delete(doc, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
        op.Document.ShouldBe(doc);
    }

    [Fact]
    public void delete_by_string_id_with_tenant_id()
    {
        var op = MartenOps.Delete<TenantTestDoc>("doc-123", TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void delete_by_guid_id_with_tenant_id()
    {
        var id = Guid.NewGuid();
        var op = MartenOps.Delete<TenantTestDoc>(id, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void delete_by_int_id_with_tenant_id()
    {
        var op = MartenOps.Delete<TenantTestDoc>(42, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void delete_by_long_id_with_tenant_id()
    {
        var op = MartenOps.Delete<TenantTestDoc>(42L, TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void delete_where_with_tenant_id()
    {
        var op = MartenOps.DeleteWhere<TenantTestDoc>(x => x.Name == "Widget", TestTenantId);

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void start_stream_with_guid_and_tenant_id()
    {
        var streamId = Guid.NewGuid();
        var op = MartenOps.StartStream<TenantTestDoc>(streamId, TestTenantId, new object());

        op.TenantId.ShouldBe(TestTenantId);
        op.StreamId.ShouldBe(streamId);
    }

    [Fact]
    public void start_stream_with_string_key_and_tenant_id()
    {
        var op = (StartStream<TenantTestDoc>)MartenOps.StartStream<TenantTestDoc>("stream-key", TestTenantId, new object());

        op.TenantId.ShouldBe(TestTenantId);
    }

    [Fact]
    public void tenant_id_null_throws_for_store()
    {
        Should.Throw<ArgumentNullException>(() =>
            MartenOps.Store(new TenantTestDoc(Guid.NewGuid(), "x"), null!));
    }

    [Fact]
    public void tenant_id_null_throws_for_insert()
    {
        Should.Throw<ArgumentNullException>(() =>
            MartenOps.Insert(new TenantTestDoc(Guid.NewGuid(), "x"), null!));
    }

    [Fact]
    public void tenant_id_null_throws_for_update()
    {
        Should.Throw<ArgumentNullException>(() =>
            MartenOps.Update(new TenantTestDoc(Guid.NewGuid(), "x"), null!));
    }

    [Fact]
    public void tenant_id_null_throws_for_delete()
    {
        Should.Throw<ArgumentNullException>(() =>
            MartenOps.Delete(new TenantTestDoc(Guid.NewGuid(), "x"), null!));
    }
}
