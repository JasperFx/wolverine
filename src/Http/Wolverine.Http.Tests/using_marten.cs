using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_marten : IntegrationContext
{
    [Fact]
    public async Task use_marten_document_session_without_outbox()
    {
        var data = new Data { Name = "foo" };

        using (var session = Store.LightweightSession())
        {
            session.Store(data);
            await session.SaveChangesAsync();
        }

        var result = await Host.GetAsJson<Data>($"/data/{data.Id}");

        result.Name.ShouldBe("foo");
    }

    [Fact]
    public async Task use_marten_document_session_with_outbox()
    {
        var input = new Data { Id = Guid.NewGuid(), Name = "Jaylen Watson" };

        var (tracked, _) = await TrackedHttpCall(x => { x.Post.Json(input).ToUrl("/publish/marten/message"); });

        var published = tracked.Sent.SingleMessage<Data>();
        published.Id.ShouldBe(input.Id);
        published.Name.ShouldBe(input.Name);

        using var session = Store.LightweightSession();
        var loaded = await session.LoadAsync<Data>(input.Id);

        loaded.ShouldNotBeNull();
    }

    public using_marten(AppFixture fixture) : base(fixture)
    {
    }
}