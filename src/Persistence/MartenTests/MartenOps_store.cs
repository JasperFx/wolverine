using Shouldly;
using Wolverine.Marten;

namespace MartenTests;

public class MartenOps_store
{
    [Fact]
    public void StoreMany()
    {
        var op = MartenOps.StoreMany(new MartenMessage2("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<MartenMessage2>();

        op.With(new MartenMessage2("Test2"));

        op.Documents.Count.ShouldBe(2);

        op.With([new MartenMessage2("Test3"), new MartenMessage2("Test4")]);

        op.Documents.Count.ShouldBe(4);

        op = MartenOps.StoreMany(new MartenMessage2("Test5"), new MartenMessage2("Test6"));

        op.Documents.Count.ShouldBe(2);

        op = MartenOps.StoreMany([new MartenMessage2("Test7"), new MartenMessage2("Test8")]);

        op.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void StoreObjects()
    {
        var op = MartenOps.StoreObjects(new MartenMessage2("Test1"));

        op.Documents.Count.ShouldBe(1);
        op.Documents[0].ShouldBeOfType<MartenMessage2>();

        op.With(new MartenMessage3("Test2"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[1].ShouldBeOfType<MartenMessage3>();

        op.With([new MartenMessage2("Test3"), new MartenMessage3("Test4")]);

        op.Documents.Count.ShouldBe(4);
        op.Documents[2].ShouldBeOfType<MartenMessage2>();
        op.Documents[3].ShouldBeOfType<MartenMessage3>();

        op = MartenOps.StoreObjects(new MartenMessage2("Test5"), new MartenMessage3("Test6"));

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<MartenMessage2>();
        op.Documents[1].ShouldBeOfType<MartenMessage3>();

        op = MartenOps.StoreObjects([new MartenMessage2("Test7"), new MartenMessage3("Test8")]);

        op.Documents.Count.ShouldBe(2);
        op.Documents[0].ShouldBeOfType<MartenMessage2>();
        op.Documents[1].ShouldBeOfType<MartenMessage3>();
    }
}