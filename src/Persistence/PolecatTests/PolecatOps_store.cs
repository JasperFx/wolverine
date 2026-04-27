using Shouldly;
using Wolverine.Polecat;

namespace PolecatTests;

public record StoreTestDoc1(string Name);
public record StoreTestDoc2(string Label);

public class PolecatOps_store
{
    [Fact]
    public void StoreMany()
    {
        var op = PolecatOps.StoreMany(new StoreTestDoc1("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();

        op.With(new StoreTestDoc1("Test2"));

        op.Documents.Count.ShouldBe(2);

        op.With([new StoreTestDoc1("Test3"), new StoreTestDoc1("Test4")]);

        op.Documents.Count.ShouldBe(4);

        op = PolecatOps.StoreMany(new StoreTestDoc1("Test5"), new StoreTestDoc1("Test6"));

        op.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void StoreObjects()
    {
        var op = PolecatOps.StoreObjects(new StoreTestDoc1("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();

        op.With(new StoreTestDoc2("Test2"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();

        op.With([new StoreTestDoc1("Test3"), new StoreTestDoc2("Test4")]);

        op.Documents.Count.ShouldBe(4);
        op.Documents[2].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[3].ShouldBeOfType<StoreTestDoc2>();

        op = PolecatOps.StoreObjects(new StoreTestDoc1("Test5"), new StoreTestDoc2("Test6"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();

        op = PolecatOps.StoreObjects([new StoreTestDoc1("Test7"), new StoreTestDoc2("Test8")]);

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();
    }

    [Fact]
    public void StoreObjects_with_tenantId()
    {
        var op = PolecatOps.StoreObjects("tenant-1", new StoreTestDoc1("Test1"), new StoreTestDoc2("Test2"));

        op.Documents.Count.ShouldBe(2);
        op.TenantId.ShouldBe("tenant-1");
    }

    [Fact]
    public void IDocumentsOp_exposes_documents_readonly()
    {
        IDocumentsOp op = PolecatOps.StoreObjects(new StoreTestDoc1("Test1"), new StoreTestDoc2("Test2"));
        op.Documents.Count.ShouldBe(2);
    }
}
