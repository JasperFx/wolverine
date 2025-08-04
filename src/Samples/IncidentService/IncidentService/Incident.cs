using JasperFx.Events;
using Marten.Events;

namespace IncidentService;

#region sample_incident_events

public record IncidentLogged(
    Guid CustomerId,
    Contact Contact,
    string Description,
    Guid LoggedBy
);

public record IncidentCategorised(
    Guid IncidentId,
    IncidentCategory Category,
    Guid CategorisedBy
);

public record IncidentPrioritised(
    Guid IncidentId,
    IncidentPriority Priority,
    Guid PrioritisedBy,
    DateTimeOffset PrioritisedAt
);

public record IncidentClosed(
    Guid ClosedBy
);

#endregion

public record AgentAssignedToIncident(
    Guid IncidentId,
    Guid AgentId,
    DateTimeOffset AssignedAt
);

public record AgentRespondedToIncident(
    Guid IncidentId,
    IncidentResponse.FromAgent Response,
    DateTimeOffset RespondedAt
);

public record CustomerRespondedToIncident(
    Guid IncidentId,
    IncidentResponse.FromCustomer Response,
    DateTimeOffset RespondedAt
);

public record IncidentResolved(
    Guid IncidentId,
    ResolutionType Resolution,
    Guid ResolvedBy,
    DateTimeOffset ResolvedAt
);

public record ResolutionAcknowledgedByCustomer(
    Guid IncidentId,
    Guid AcknowledgedBy,
    DateTimeOffset AcknowledgedAt
);



public enum IncidentStatus
{
    Pending = 1,
    Resolved = 8,
    ResolutionAcknowledgedByCustomer = 16,
    Closed = 32
}

#region sample_Incident_aggregate

public class Incident
{
    public Guid Id { get; set; }
    
    // THIS IS IMPORTANT! Marten will set this itself, and you
    // can use this to communicate the current version to clients
    // as a way to opt into optimistic concurrency checks to prevent
    // problems from concurrent access
    public int Version { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Pending;
    public IncidentCategory? Category { get; set; }
    public bool HasOutstandingResponseToCustomer { get; set; } = false;

    // Make serialization easy
    public Incident()
    {
    }

    public void Apply(IncidentLogged _) { }
    public void Apply(AgentRespondedToIncident _) => HasOutstandingResponseToCustomer = false;

    public void Apply(CustomerRespondedToIncident _) => HasOutstandingResponseToCustomer = true;

    public void Apply(IncidentResolved _) => Status = IncidentStatus.Resolved;

    public void Apply(ResolutionAcknowledgedByCustomer _) => Status = IncidentStatus.ResolutionAcknowledgedByCustomer;

    public void Apply(IncidentClosed _) => Status = IncidentStatus.Closed;

    public bool ShouldDelete(Archived @event) => true;
}

#endregion

public enum IncidentCategory
{
    Software,
    Hardware,
    Network,
    Database
}

public enum IncidentPriority
{
    Critical,
    High,
    Medium,
    Low
}

public enum ResolutionType
{
    Temporary,
    Permanent,
    NotAnIncident
}

public enum ContactChannel
{
    Email,
    Phone,
    InPerson,
    GeneratedBySystem
}

public record Contact(
    ContactChannel ContactChannel,
    string? FirstName = null,
    string? LastName = null,
    string? EmailAddress = null,
    string? PhoneNumber = null
);

[Obsolete("Get rid of this")]
public abstract record IncidentResponse
{
    public record FromAgent(
        Guid AgentId,
        string Content,
        bool VisibleToCustomer
    ): IncidentResponse(Content);

    public record FromCustomer(
        Guid CustomerId,
        string Content
    ): IncidentResponse(Content);

    public string Content { get; init; }

    private IncidentResponse(string content)
    {
        Content = content;
    }
}
