namespace LoadTesting.Trips;

public record RepairRequested(Guid TripId, string State);
public record ConductRepairs(Guid TripId);
public record RepairsCompleted(Guid TripId);

public record TripResumed;