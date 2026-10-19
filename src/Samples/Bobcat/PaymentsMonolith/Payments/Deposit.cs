using FluentValidation;
using Marten;
using PaymentsMonolith;
using Wolverine.Http;

namespace Payments;

public class Deposit
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DepositStatus Status { get; set; } = DepositStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum DepositStatus { Pending, Completed, Rejected }

public record CreateDeposit(Guid CustomerId, string Currency, decimal Amount)
{
    public class Validator : AbstractValidator<CreateDeposit>
    {
        public Validator()
        {
            RuleFor(x => x.CustomerId).NotEmpty();
            RuleFor(x => x.Currency).NotEmpty();
            RuleFor(x => x.Amount).GreaterThan(0);
        }
    }
}

public static class CreateDepositEndpoint
{
    [WolverinePost("/api/payments/deposits")]
    public static (Deposit, DepositCompleted) Post(CreateDeposit command, IDocumentSession session)
    {
        var deposit = new Deposit
        {
            Id = Guid.NewGuid(),
            CustomerId = command.CustomerId,
            Currency = command.Currency,
            Amount = command.Amount,
            Status = DepositStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(deposit);

        return (deposit, new DepositCompleted(deposit.Id, deposit.CustomerId, deposit.Currency, deposit.Amount));
    }
}
