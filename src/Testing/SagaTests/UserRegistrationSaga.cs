using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaTests;

public class UserRegistrationSaga : Saga
{
  public string? Id { get; set; }

  public async Task Start(
    Registered registered,
    IMessageBus bus
  )
  {
    var (companyName, _, _, _, subscriptionId) = registered;
    Id = subscriptionId;
    await bus.InvokeAsync(new Subscribed(companyName, subscriptionId));
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