using Marten.Metadata;
using Wolverine.Persistence.Sagas;

namespace MartenTests.Saga;

public class UserRegistrationSaga : Wolverine.Saga, IRevisioned
{
    public string? Id { get; set; }

    public Subscribed Start(
        Registered registered
    )
    {
        var (companyName, _, _, _, subscriptionId) = registered;
        Id = subscriptionId;
        return new Subscribed(companyName, subscriptionId);
    }

    public void Handle(
        Subscribed subscribed
    )
    {
        var (companyName, subscriptionId) = subscribed;
    }

    public static void NotFound(Subscribed subscribed)
    {
        NotFoundSubscribed = subscribed;
    }

    public static Subscribed NotFoundSubscribed { get; set; }
}

public record Subscribed(
    string CompanyName,
    [property: SagaIdentity] string SubscriptionId
);

public record Registered(
    string CompanyName,
    string Firstname,
    string Lastname,
    string Email,
    [property: SagaIdentity] string SubscriptionId
);