using Wolverine.Persistence.Sagas;

namespace PersistenceTests.Marten.Saga;

public class UserRegistrationSaga : Wolverine.Saga
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