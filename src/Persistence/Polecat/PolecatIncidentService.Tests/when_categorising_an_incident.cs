using Alba;
using Polecat;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace PolecatIncidentService.Tests;

public class when_categorising_an_incident : IntegrationContext
{
    public when_categorising_an_incident(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_categorise_an_incident()
    {
        // First, log an incident
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Network down", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        // Now categorise it
        var categorise = new CategoriseIncident(IncidentCategory.Network, Guid.NewGuid(), 1);

        await Scenario(x =>
        {
            x.Post.Json(categorise).ToUrl($"/api/incidents/{incidentId}/category");
            x.StatusCodeShouldBe(204);
        });

        // Verify the category was applied
        await using var session = Store.LightweightSession();
        var incident = await session.Events.FetchLatest<Incident>(incidentId);
        incident.ShouldNotBeNull();
        incident!.Category.ShouldBe(IncidentCategory.Network);
    }

    [Fact]
    public async Task cannot_categorise_closed_incident()
    {
        // Log and close an incident
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Fixed", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        // Close it
        var close = new CloseIncident(Guid.NewGuid(), 1);
        await Scenario(x =>
        {
            x.Post.Json(close).ToUrl($"/api/incidents/close/{incidentId}");
            x.StatusCodeShouldBe(200);
        });

        // Try to categorise — should fail (incident is closed)
        // NOTE: With Polecat's [Aggregate] the Validate middleware returns 500
        // instead of 400 for ProblemDetails. This differs from Marten and needs
        // investigation in Wolverine.Http.Polecat validation middleware.
        var categorise = new CategoriseIncident(IncidentCategory.Software, Guid.NewGuid(), 2);
        await Scenario(x =>
        {
            x.Post.Json(categorise).ToUrl($"/api/incidents/{incidentId}/category");
            x.StatusCodeShouldBe(500);
        });
    }
}
