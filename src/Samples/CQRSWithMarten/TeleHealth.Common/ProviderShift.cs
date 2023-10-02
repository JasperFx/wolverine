using Marten;

namespace TeleHealth.Common;

public class ProviderShift
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Guid BoardId { get; private set; }
    public Guid ProviderId { get; init; }
    public ProviderStatus Status { get; private set; }
    public string Name { get; init; }
    public Guid? AppointmentId { get; set; }

    public static async Task<ProviderShift> Create(
        ProviderJoined joined,
        IQuerySession session)
    {
        var provider = await session
            .LoadAsync<Provider>(joined.ProviderId);

        return new ProviderShift
        {
            Name = $"{provider.FirstName} {provider.LastName}",
            Status = ProviderStatus.Ready,
            ProviderId = joined.ProviderId,
            BoardId = joined.BoardId
        };
    }

    public void Apply(ProviderReady ready)
    {
        AppointmentId = null;
        Status = ProviderStatus.Ready;
    }

    public void Apply(ProviderAssigned assigned)
    {
        Status = ProviderStatus.Assigned;
        AppointmentId = assigned.AppointmentId;
    }

    public void Apply(ProviderPaused paused)
    {
        Status = ProviderStatus.Paused;
        AppointmentId = null;
    }

    // This is kind of a catch all for any paperwork the
    // provider has to do after an appointment has ended
    // for the just concluded appointment
    public void Apply(ChartingStarted charting)
    {
        Status = ProviderStatus.Charting;
    }
}