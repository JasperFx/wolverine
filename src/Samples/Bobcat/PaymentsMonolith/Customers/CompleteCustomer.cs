using FluentValidation;
using Marten;
using PaymentsMonolith;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Customers;

public record CompleteCustomer(Guid CustomerId, string Name, string FullName, string Nationality)
{
    public class Validator : AbstractValidator<CompleteCustomer>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.FullName).NotEmpty();
            RuleFor(x => x.Nationality).NotEmpty();
        }
    }
}

public static class CompleteCustomerEndpoint
{
    [WolverinePut("/api/customers/{customerId}/complete")]
    public static (Customer, CustomerCompleted) Put(
        CompleteCustomer command,
        [Entity("CustomerId", Required = true)] Customer customer,
        IDocumentSession session)
    {
        customer.Name = command.Name;
        customer.FullName = command.FullName;
        customer.Nationality = command.Nationality;
        customer.IsCompleted = true;

        session.Store(customer);

        return (customer, new CustomerCompleted(customer.Id, customer.Name, customer.FullName, customer.Nationality));
    }
}

/// <summary>
/// Handles UserCreated from the Users module — creates a Customer stub.
/// </summary>
public static class UserCreatedHandler
{
    public static void Handle(UserCreated message, IDocumentSession session)
    {
        session.Store(new Customer
        {
            Id = message.UserId,
            Email = message.Email,
            FullName = message.FullName,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
