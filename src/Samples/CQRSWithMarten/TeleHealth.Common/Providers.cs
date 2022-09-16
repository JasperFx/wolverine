namespace TeleHealth.Common;

public record ProviderAssigned(Guid AppointmentId);

public record ProviderJoined(Guid BoardId, Guid ProviderId);

public record ProviderReady;

public record ProviderPaused;

public record ProviderSignedOff;

public record ChartingFinished;

public record ChartingStarted;

public enum ProviderStatus
{
    Ready,
    Assigned,
    Charting,
    Paused
}

public class Provider
{
    public Guid Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
}
