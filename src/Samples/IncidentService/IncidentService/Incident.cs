using Marten.Events;

namespace IncidentService;

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

public record IncidentClosed(
    Guid ClosedBy
);

public enum IncidentStatus
{
    Pending = 1,
    Resolved = 8,
    ResolutionAcknowledgedByCustomer = 16,
    Closed = 32
}

public record Incident(
    Guid Id,
    IncidentStatus Status,
    bool HasOutstandingResponseToCustomer = false
)
{
    public static Incident Create(IEvent<IncidentLogged> logged) =>
        new(logged.StreamId, IncidentStatus.Pending);

    public Incident Apply(AgentRespondedToIncident agentResponded) =>
        this with { HasOutstandingResponseToCustomer = false };

    public Incident Apply(CustomerRespondedToIncident customerResponded) =>
        this with { HasOutstandingResponseToCustomer = true };

    public Incident Apply(IncidentResolved resolved) =>
        this with { Status = IncidentStatus.Resolved };

    public Incident Apply(ResolutionAcknowledgedByCustomer acknowledged) =>
        this with { Status = IncidentStatus.ResolutionAcknowledgedByCustomer };

    public Incident Apply(IncidentClosed closed) =>
        this with { Status = IncidentStatus.Closed };
}

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
