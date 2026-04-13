using Shouldly;
using Wolverine.Marten;

namespace MartenTests;

public record StoreTestDoc1(string Name);
public record StoreTestDoc2(string Label);

public class MartenOps_store
{
    [Fact]
    public void StoreMany()
    {
        var op = MartenOps.StoreMany(new StoreTestDoc1("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();

        op.With(new StoreTestDoc1("Test2"));

        op.Documents.Count.ShouldBe(2);

        op.With([new StoreTestDoc1("Test3"), new StoreTestDoc1("Test4")]);

        op.Documents.Count.ShouldBe(4);

        op = MartenOps.StoreMany(new StoreTestDoc1("Test5"), new StoreTestDoc1("Test6"));

        op.Documents.Count.ShouldBe(2);

        op = MartenOps.StoreMany([new StoreTestDoc1("Test7"), new StoreTestDoc1("Test8")]);

        op.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void StoreObjects()
    {
        var op = MartenOps.StoreObjects(new StoreTestDoc1("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();

        op.With(new StoreTestDoc2("Test2"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();

        op.With([new StoreTestDoc1("Test3"), new StoreTestDoc2("Test4")]);

        op.Documents.Count.ShouldBe(4);
        op.Documents[2].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[3].ShouldBeOfType<StoreTestDoc2>();

        op = MartenOps.StoreObjects(new StoreTestDoc1("Test5"), new StoreTestDoc2("Test6"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();

        op = MartenOps.StoreObjects([new StoreTestDoc1("Test7"), new StoreTestDoc2("Test8")]);

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<StoreTestDoc1>();
        op.Documents[1].ShouldBeOfType<StoreTestDoc2>();
    }
}
