using FluentValidation;
using Marten;
using MeetingGroupMonolith;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Payments;

public record CreateSubscription(Guid PayerId, string Period)
{
    public class Validator : AbstractValidator<CreateSubscription>
    {
        public Validator()
        {
            RuleFor(x => x.PayerId).NotEmpty();
            RuleFor(x => x.Period).NotEmpty().Must(p => p is "Monthly" or "HalfYearly" or "Yearly");
        }
    }
}

/// <summary>201 Created with a Location header pointing at the new subscription.</summary>
public record SubscriptionCreation(Guid Id) : CreationResponse($"/api/payments/subscriptions/{Id}");

public static class CreateSubscriptionEndpoint
{
    // Event-sourced: starts a new event stream in Marten's event store.
    // Cascading message notifies the Meetings module of the subscription change.
    [WolverinePost("/api/payments/subscriptions")]
    public static (SubscriptionCreation, SubscriptionExpirationChangedEvent) Post(
        CreateSubscription command,
        IDocumentSession session)
    {
        var subscriptionId = Guid.NewGuid();
        var expirationDate = command.Period switch
        {
            "Monthly" => DateTime.UtcNow.AddMonths(1),
            "HalfYearly" => DateTime.UtcNow.AddMonths(6),
            "Yearly" => DateTime.UtcNow.AddYears(1),
            _ => DateTime.UtcNow.AddMonths(1),
        };

        // Start a new event stream — Marten stores the event and
        // the Subscription snapshot is built via the Apply methods
        session.Events.StartStream<Subscription>(
            subscriptionId,
            new SubscriptionCreated(subscriptionId, command.PayerId, command.Period, expirationDate));

        return (new SubscriptionCreation(subscriptionId), new SubscriptionExpirationChangedEvent(command.PayerId, expirationDate));
    }
}

/// <summary>
/// Reads the inline snapshot of the subscription aggregate — the document Marten rebuilt from
/// the stream's events, which is the claim an event-sourced module exists to make.
/// </summary>
public static class GetSubscriptionEndpoint
{
    [WolverineGet("/api/payments/subscriptions/{id}")]
    public static Subscription? Get(Guid id, [Entity] Subscription? subscription) => subscription;
}
