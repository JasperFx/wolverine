namespace OutboxDemo;

/// <summary>
/// Sent by the saga to trigger email notification.
/// </summary>
public record SendRegistrationEmail(
    Guid RegistrationId,
    DateTime RegistrationDate,
    string MemberId,
    string EventId
);

public static class SendRegistrationEmailHandler
{
    public static void Handle(SendRegistrationEmail message)
    {
        Console.WriteLine($"[SendEmail] Registration email sent to member {message.MemberId} for event {message.EventId}");
    }
}
