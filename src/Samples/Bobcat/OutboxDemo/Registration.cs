using Wolverine;

namespace OutboxDemo;

/// <summary>
/// Published when a new registration is submitted.
/// Triggers the saga workflow.
/// </summary>
public record RegistrationSubmitted(
    Guid RegistrationId,
    DateTime RegistrationDate,
    string MemberId,
    string EventId,
    decimal Payment
);

/// <summary>
/// Registration document stored in Marten.
/// </summary>
public class Registration : Saga
{
    public Guid Id { get; set; }
    public DateTime RegistrationDate { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public decimal Payment { get; set; }
    public string CurrentState { get; set; } = "Started";
    
    public (SendRegistrationEmail, AddEventAttendee) Handle(RegistrationSubmitted message)
    {
        // Just to show state changes
        CurrentState = "Submitted";
        
        // Cascade messages — sent via the Marten outbox in the same transaction
        return (
            new SendRegistrationEmail(message.RegistrationId, message.RegistrationDate, message.MemberId, message.EventId),
            new AddEventAttendee(message.RegistrationId, message.MemberId, message.EventId)
        );
    }
    
    public void Handle(RegistrationValidated validated)
    {
        CurrentState = "Validated";
    }
}
