using Wolverine.Http;
using Wolverine.Polecat;

namespace PolecatIncidentService;

#region sample_polecat_logincident
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
        var start = PolecatOps.StartStream<Incident>(logged);

        var response = new CreationResponse<Guid>("/api/incidents/" + start.StreamId, start.StreamId);

        return (response, start);
    }
}

#endregion
