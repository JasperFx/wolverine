using IncidentService;

namespace Helpdesk.Api.Incidents;

public record PrioritiseIncident(
    Guid IncidentId,
    IncidentPriority Priority,
    Guid PrioritisedBy,
    DateTimeOffset Now
);

public record AssignAgentToIncident(
    Guid IncidentId,
    Guid AgentId,
    DateTimeOffset Now
);

public record RecordAgentResponseToIncident(
    Guid IncidentId,
    IncidentResponse.FromAgent Response,
    DateTimeOffset Now
);

public record RecordCustomerResponseToIncident(
    Guid IncidentId,
    IncidentResponse.FromCustomer Response,
    DateTimeOffset Now
);

public record ResolveIncident(
    Guid IncidentId,
    ResolutionType Resolution,
    Guid ResolvedBy,
    DateTimeOffset Now
);

public record AcknowledgeResolution(
    Guid IncidentId,
    Guid AcknowledgedBy,
    DateTimeOffset Now
);

internal static class IncidentService
{

    public static IncidentPrioritised Handle(Incident current, PrioritiseIncident command)
    {
        if (current.Status == IncidentStatus.Closed)
            throw new InvalidOperationException("Incident is already closed");

        var (incidentId, incidentPriority, prioritisedBy, now) = command;

        return new IncidentPrioritised(incidentId, incidentPriority, prioritisedBy, now);
    }

    public static AgentAssignedToIncident Handle(Incident current, AssignAgentToIncident command)
    {
        if (current.Status == IncidentStatus.Closed)
            throw new InvalidOperationException("Incident is already closed");

        var (incidentId, agentId, now) = command;

        return new AgentAssignedToIncident(incidentId, agentId, now);
    }

    public static AgentRespondedToIncident Handle(
        Incident current,
        RecordAgentResponseToIncident command
    )
    {
        if (current.Status == IncidentStatus.Closed)
            throw new InvalidOperationException("Incident is already closed");

        var (incidentId, response, now) = command;

        return new AgentRespondedToIncident(incidentId, response, now);
    }

    public static CustomerRespondedToIncident Handle(
        Incident current,
        RecordCustomerResponseToIncident command
    )
    {
        if (current.Status == IncidentStatus.Closed)
            throw new InvalidOperationException("Incident is already closed");

        var (incidentId, response, now) = command;

        return new CustomerRespondedToIncident(incidentId, response, now);
    }

    public static IncidentResolved Handle(
        Incident current,
        ResolveIncident command
    )
    {
        if (current.Status is IncidentStatus.Resolved or IncidentStatus.Closed)
            throw new InvalidOperationException("Cannot resolve already resolved or closed incident");

        if (current.HasOutstandingResponseToCustomer)
            throw new InvalidOperationException("Cannot resolve incident that has outstanding responses to customer");

        var (incidentId, resolution, resolvedBy, now) = command;

        return new IncidentResolved(incidentId, resolution, resolvedBy, now);
    }

    public static ResolutionAcknowledgedByCustomer Handle(
        Incident current,
        AcknowledgeResolution command
    )
    {
        if (current.Status is not IncidentStatus.Resolved)
            throw new InvalidOperationException("Only resolved incident can be acknowledged");

        var (incidentId, acknowledgedBy, now) = command;

        return new ResolutionAcknowledgedByCustomer(incidentId, acknowledgedBy, now);
    }
}
