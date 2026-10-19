namespace MeetingGroupMonolith;

// Integration events for cross-module communication via durable local queues.
// Replaces the custom InMemoryEventBus + OutboxMessages + InboxMessages + Quartz jobs.

public record NewUserRegisteredEvent(Guid UserId, string Login, string Email, string FirstName, string LastName);
public record MeetingGroupProposalAcceptedEvent(Guid ProposalId, string Name, string Description, string LocationCity, string LocationCountryCode, Guid ProposalUserId);
public record MeetingAttendeeAddedEvent(Guid MeetingId, Guid AttendeeId, Guid MeetingGroupId);
public record MeetingFeePaidEvent(Guid MeetingFeeId, Guid PaymentId);
public record SubscriptionExpirationChangedEvent(Guid PayerId, DateTime ExpirationDate);
