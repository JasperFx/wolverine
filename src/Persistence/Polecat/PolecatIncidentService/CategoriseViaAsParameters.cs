using JasperFx.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Polecat;

namespace PolecatIncidentService;

// GH-3135 repro for the Polecat integration: an [AsParameters] object carries the conventional
// aggregate-id route value ({incidentId}) AND a [FromBody] payload, while [WriteAggregate]
// IEventStream<Incident> resolves the stream from that same id. This mirrors the Marten repro and
// shares the same Wolverine.Http core codegen path (AuditToActivityFrame over the inferred
// aggregate-id identity member).
public record CategoriseIncidentPayload(IncidentCategory Category, Guid CategorisedBy);

public record CategoriseViaAsParameters([FromRoute] Guid IncidentId, [FromBody] CategoriseIncidentPayload Body);

public record IncidentCategorisedResponse(Guid IncidentId, IncidentCategory Category);

public static class CategoriseViaAsParametersEndpoint
{
    [WolverinePost("/api/incidents/asparameters/{incidentId}/category")]
    public static IncidentCategorisedResponse Post(
        [AsParameters] CategoriseViaAsParameters command,
        [WriteAggregate] IEventStream<Incident> stream)
    {
        stream.AppendOne(new IncidentCategorised(command.IncidentId, command.Body.Category, command.Body.CategorisedBy));
        return new IncidentCategorisedResponse(command.IncidentId, command.Body.Category);
    }
}
