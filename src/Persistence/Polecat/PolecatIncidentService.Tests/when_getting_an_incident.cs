using Alba;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace PolecatIncidentService.Tests;

public class when_getting_an_incident : IntegrationContext
{
    public when_getting_an_incident(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_get_incident_by_id()
    {
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Disk full", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        var result = await Scenario(x =>
        {
            x.Get.Url($"/api/incidents/{incidentId}");
            x.StatusCodeShouldBe(200);
        });

        var incident = result.ReadAsJson<Incident>();
        incident.ShouldNotBeNull();
        incident!.Status.ShouldBe(IncidentStatus.Pending);
    }

    [Fact]
    public async Task returns_404_for_nonexistent_incident()
    {
        await Scenario(x =>
        {
            x.Get.Url($"/api/incidents/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }
}
