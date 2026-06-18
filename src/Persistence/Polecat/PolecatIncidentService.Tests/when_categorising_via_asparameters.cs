using Alba;
using Polecat;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace PolecatIncidentService.Tests;

// GH-3135: the route id flows through an [AsParameters] object (with a [FromBody] payload) while
// [WriteAggregate] IEventStream<Incident> resolves the stream from that same id. This shares the
// Wolverine.Http core codegen path that 500'd under Marten, so it verifies the fix on Polecat too.
public class when_categorising_via_asparameters : IntegrationContext
{
    public when_categorising_via_asparameters(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_categorise_with_id_from_asparameters_route()
    {
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Network down", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        var payload = new CategoriseIncidentPayload(IncidentCategory.Network, Guid.NewGuid());

        var result = await Scenario(x =>
        {
            x.Post.Json(payload).ToUrl($"/api/incidents/asparameters/{incidentId}/category");
            x.StatusCodeShouldBe(200);
        });

        var response = result.ReadAsJson<IncidentCategorisedResponse>();
        response.IncidentId.ShouldBe(incidentId);
        response.Category.ShouldBe(IncidentCategory.Network);

        await using var session = Store.LightweightSession();
        var incident = await session.Events.FetchLatest<Incident>(incidentId);
        incident.ShouldNotBeNull();
        incident!.Category.ShouldBe(IncidentCategory.Network);
    }
}
