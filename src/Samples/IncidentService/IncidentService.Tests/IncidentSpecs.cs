using Bobcat;
using Marten;
using Bobcat.Xunit;
using Helpdesk.Api.Incidents;
using Shouldly;
using Wolverine.Bobcat;
using Wolverine.Bobcat.Http;
using Wolverine.Http;
using Xunit;

namespace IncidentService.Tests;

/// <summary>
/// The incident lifecycle as Bobcat specifications: the same host and the same Marten reset as
/// <see cref="IntegrationContext" />, with the Given/When/Then vocabulary of WolverineFx.Bobcat.
/// </summary>
[Collection("integration")]
public abstract class IncidentSpec(AppFixture fixture) : WolverineHttpSpec(fixture.Host!), IAsyncLifetime
{
    protected static readonly Guid Agent = Guid.NewGuid();

    protected static IncidentLogged Logged(Guid customerId)
        => new(customerId, new Contact(ContactChannel.Email, EmailAddress: "ann@example.com"), "It's broken", Agent);

    public async ValueTask InitializeAsync() => await AlbaHost.ResetAllMartenDataAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[BobcatFeature("Logging an incident")]
public class logging_an_incident(AppFixture fixture) : IncidentSpec(fixture)
{
    [Fact]
    public async Task a_new_incident_starts_its_own_stream()
    {
        var customer = Guid.NewGuid();
        var contact = new Contact(ContactChannel.Email, EmailAddress: "ann@example.com");

        // The route comes from LogIncident's endpoint; the id is minted by the endpoint itself
        var response = await WhenPosted<CreationResponse<Guid>>(new LogIncident(customer, contact, "It's broken", Agent));

        ThenResponseIs(201);
        ThenEvents(new IncidentLogged(customer, contact, "It's broken", Agent));
        await ThenStreamIsStarted<Incident>(response!.Value);

        var incident = await ThenReadModel<Incident>(response.Value);
        Verify(incident, """
                         | Status  | Category |
                         | Pending | NULL     |
                         """);
    }
}

[BobcatFeature("Categorising an incident")]
public class categorising_an_incident(AppFixture fixture) : IncidentSpec(fixture)
{
    [Fact]
    public async Task an_open_incident_is_categorised()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Incident>(id, Logged(Guid.NewGuid()));

        await WhenPosted(new CategoriseIncident(IncidentCategory.Database, Agent, 1), $"/api/incidents/{id}/category");

        ThenResponseIs(204);
        ThenEvents(new IncidentCategorised(id, IncidentCategory.Database, Agent));
    }

    [Fact]
    public async Task a_closed_incident_is_refused()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Incident>(id, Logged(Guid.NewGuid()), new IncidentClosed(Agent));

        await WhenPosted(new CategoriseIncident(IncidentCategory.Database, Agent, 2), $"/api/incidents/{id}/category");

        ThenRefusedWith("Incident is already closed");
        ThenNoEvents();
    }
}

[BobcatFeature("Closing an incident")]
public class closing_an_incident(AppFixture fixture) : IncidentSpec(fixture)
{
    [Fact]
    public async Task an_open_incident_is_closed_and_archived_later()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Incident>(id, Logged(Guid.NewGuid()));

        await WhenPosted(new CloseIncident(Agent, 1), $"/api/incidents/close/{id}");

        ThenEvents(new IncidentClosed(Agent));
        ThenMessageScheduled<ArchiveIncident>(TimeSpan.FromDays(3));
    }

    [Fact]
    public async Task closing_a_closed_incident_appends_nothing_and_archives_nothing()
    {
        // The conditional append: the endpoint returns no events when the incident is already closed
        var id = Guid.NewGuid();
        await GivenEvents<Incident>(id, Logged(Guid.NewGuid()), new IncidentClosed(Agent));

        await WhenPosted(new CloseIncident(Agent, 2), $"/api/incidents/close/{id}");

        ThenResponseIs(200);
        ThenNoEvents();
        ThenNoMessageScheduled<ArchiveIncident>();
    }
}
