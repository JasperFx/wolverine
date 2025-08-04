using Marten;
using Shouldly;
using WolverineWebApi.Things;

namespace Wolverine.Http.Tests.Marten;

public class using_ancillary_stores : IntegrationContext
{
    public using_ancillary_stores(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task create_new_thing_with_different_identity()
    {
        var request = new ThingEndpoints.CreateThingRequest("Item 1");

        var result = await Host.Scenario(s =>
        {
            s.Post.Json(request).ToUrl("/things");
            s.StatusCodeShouldBe(201);
        });

        var response = result.ReadAsJson<ThingEndpoints.ThingCreationResponse>();

        var store = Host.DocumentStore<IThingStore>();
        using var session = store.LightweightSession();

        var thing = await session.Events.FetchLatest<Thing>(response.Id);
        thing.ShouldNotBeNull();
    }
}