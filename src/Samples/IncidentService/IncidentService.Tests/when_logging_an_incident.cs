using Alba;
using Helpdesk.Api.Incidents;
using Marten;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace IncidentService.Tests;

public class when_logging_an_incident : IntegrationContext
{
    public when_logging_an_incident(AppFixture fixture) : base(fixture)
    {
    }

    #region sample_unit_test_log_incident

    [Fact]
    public void unit_test()
    {
        var contact = new Contact(ContactChannel.Email);
        var command = new LogIncident(Guid.NewGuid(), contact, "It's broken", Guid.NewGuid());

        // Pure function FTW!
        var (response, startStream) = LogIncidentEndpoint.Post(command);
        
        // Should only have the one event
        startStream.Events.ShouldBe([
            new IncidentLogged(command.CustomerId, command.Contact, command.Description, command.LoggedBy)
        ]);
    }

    #endregion

    #region sample_end_to_end_on_log_incident

    [Fact]
    public async Task happy_path_end_to_end()
    {
        var contact = new Contact(ContactChannel.Email);
        var command = new LogIncident(Guid.NewGuid(), contact, "It's broken", Guid.NewGuid());
        
        // Log a new incident first
        var initial = await Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/incidents");
            x.StatusCodeShouldBe(201);
        });

        // Read the response body by deserialization
        var response = initial.ReadAsJson<CreationResponse<Guid>>();

        // Reaching into Marten to build the current state of the new Incident
        // just to check the expected outcome
        using var session = Host.DocumentStore().LightweightSession();
        var incident = await session.Events.AggregateStreamAsync<Incident>(response.Value);
        
        incident.Status.ShouldBe(IncidentStatus.Pending);
    }

    #endregion
}