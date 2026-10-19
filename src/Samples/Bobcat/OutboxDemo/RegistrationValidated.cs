namespace OutboxDemo;

/// <summary>
/// Published after registration validation completes.
/// </summary>
public record RegistrationValidated(Guid RegistrationId);

public static class RegistrationValidatedHandler
{
    public static void Handle(RegistrationValidated message)
    {
        Console.WriteLine($"[RegistrationValidated] Registration {message.RegistrationId} validated successfully");
    }
}
