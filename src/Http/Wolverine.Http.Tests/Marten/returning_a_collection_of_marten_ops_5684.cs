using Alba;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// GH-5684. Returning a collection of IMartenOp from an HTTP endpoint used to write the ops out as the JSON
// response body and never execute them
public class returning_a_collection_of_marten_ops_5684 : IntegrationContext
{
    public returning_a_collection_of_marten_ops_5684(AppFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData("list")]
    [InlineData("enumerable")]
    [InlineData("array")]
    public async Task the_ops_are_executed_and_nothing_is_written_to_the_response(string shape)
    {
        var id = Guid.NewGuid();

        var result = await Scenario(x =>
        {
            x.Post.Url($"/marten-ops/{shape}/{id}");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();

        await using var session = Store.LightweightSession();
        var note = await session.LoadAsync<SideEffectNote>(id, TestContext.Current.CancellationToken);
        note.ShouldNotBeNull();
        note.Text.ShouldBe(shape);
    }
}
