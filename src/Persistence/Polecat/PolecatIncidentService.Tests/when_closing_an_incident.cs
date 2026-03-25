using Alba;
using Polecat;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace PolecatIncidentService.Tests;

public class when_closing_an_incident : IntegrationContext
{
    public when_closing_an_incident(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_close_an_incident()
    {
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Server crash", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        var close = new CloseIncident(Guid.NewGuid(), 1);
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(close).ToUrl($"/api/incidents/close/{incidentId}");
            x.StatusCodeShouldBe(200);
        });

        var incident = result.ReadAsJson<Incident>();
        incident.ShouldNotBeNull();
        incident!.Status.ShouldBe(IncidentStatus.Closed);

        // Should have scheduled an ArchiveIncident message
        tracked.Sent.SingleMessage<ArchiveIncident>()
            .IncidentId.ShouldBe(incidentId);
    }

    [Fact]
    public async Task closing_is_idempotent()
    {
        var contact = new Contact(ContactChannel.Email);
        var logCommand = new LogIncident(Guid.NewGuid(), contact, "Already fixed", Guid.NewGuid());

        var initial = await Scenario(x =>
        {
            x.Post.Json(logCommand).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        var incidentId = initial.ReadAsJson<CreationResponse<Guid>>().Value;

        // Close once
        var close = new CloseIncident(Guid.NewGuid(), 1);
        await Scenario(x =>
        {
            x.Post.Json(close).ToUrl($"/api/incidents/close/{incidentId}");
            x.StatusCodeShouldBe(200);
        });

        // Close again — should succeed (idempotent)
        var closeAgain = new CloseIncident(Guid.NewGuid(), 2);
        await Scenario(x =>
        {
            x.Post.Json(closeAgain).ToUrl($"/api/incidents/close/{incidentId}");
            x.StatusCodeShouldBe(200);
        });
    }
}
