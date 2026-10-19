using Wolverine.Http;
using Wolverine.Persistence;

namespace Customers;

/// <summary>
/// Reading a customer back was missing entirely — the module could only be written to, via the
/// UserCreated cascade and the completion endpoint. Without it there is no way to observe that
/// registering a user really does create the customer stub, which is the sample's whole point.
/// </summary>
public static class GetCustomerEndpoint
{
    [WolverineGet("/api/customers/{id}")]
    public static Customer? Get(Guid id, [Entity] Customer? customer) => customer;
}
