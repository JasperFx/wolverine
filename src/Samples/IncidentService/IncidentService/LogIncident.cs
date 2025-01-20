using Helpdesk.Api.Incidents;
using Wolverine.Http;
using Wolverine.Marten;

namespace IncidentService;

public record LogIncident(
    Guid CustomerId,
    Contact Contact,
    string Description,
    Guid LoggedBy
);

public static class LogIncidentEndpoint
{
    [WolverinePost("/api/incidents")]
    public static (CreationResponse<Guid>, IStartStream) Post(LogIncident command)
    {
        var (customerId, contact, description, loggedBy) = command;

        var logged = new IncidentLogged(customerId, contact, description, loggedBy);
        var start = MartenOps.StartStream<Incident>(logged);

        var response = new CreationResponse<Guid>("/api/incidents/" + start.StreamId, start.StreamId);
        
        return (response, start);
    }
}