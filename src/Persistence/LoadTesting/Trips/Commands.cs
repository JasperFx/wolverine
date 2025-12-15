
namespace LoadTesting.Trips;

public record StartTrip(Guid TripId, int StartDay, string State);


public record RecordTravel(Guid TripId, Traveled Event);
public record AbortTrip(Guid TripId);

public record RecordBreakdown(Guid TripId, bool IsCritical);

public record MarkVacationOver(Guid TripId);

public record Arrive(Guid TripId, int Day, string State);

public record Depart(Guid TripId, int Day, string State);

public record EndTrip(Guid TripId, int Day, string State);