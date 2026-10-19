using Marten.Events.Aggregation;

namespace Payments;

// --- Domain Events (stored in Marten event store) ---

public record SubscriptionCreated(Guid SubscriptionId, Guid PayerId, string Period, DateTime ExpirationDate);
public record SubscriptionRenewed(Guid SubscriptionId, DateTime NewExpirationDate);
public record SubscriptionExpired(Guid SubscriptionId);
public record MeetingFeeCreated(Guid MeetingFeeId, Guid PayerId, Guid MeetingId, decimal Amount);
public record MeetingFeePaid(Guid MeetingFeeId, Guid PaymentId);

/// <summary>
/// Subscription aggregate — event-sourced via Marten.
/// Replaces SqlStreamStore + custom Load(IEnumerable&lt;IDomainEvent&gt;) pattern.
/// Marten rebuilds state by calling Apply methods automatically.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public Guid PayerId { get; set; }
    public string Period { get; set; } = string.Empty;
    public DateTime ExpirationDate { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    // Marten calls these automatically when replaying events
    public void Apply(SubscriptionCreated e)
    {
        Id = e.SubscriptionId;
        PayerId = e.PayerId;
        Period = e.Period;
        ExpirationDate = e.ExpirationDate;
        Status = SubscriptionStatus.Active;
    }

    public void Apply(SubscriptionRenewed e)
    {
        ExpirationDate = e.NewExpirationDate;
        Status = SubscriptionStatus.Active;
    }

    public void Apply(SubscriptionExpired _)
    {
        Status = SubscriptionStatus.Expired;
    }
}

public enum SubscriptionStatus { Active, Expired }
