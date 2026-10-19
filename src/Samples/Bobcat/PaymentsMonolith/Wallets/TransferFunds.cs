using Marten;
using Microsoft.AspNetCore.Mvc;
using PaymentsMonolith;
using Wolverine.Http;

namespace Wallets;

public record TransferFunds(Guid FromWalletId, Guid ToWalletId, decimal Amount);

public static class TransferFundsEndpoint
{
    public static async Task<ProblemDetails> ValidateAsync(TransferFunds command, IQuerySession session)
    {
        var from = await session.LoadAsync<Wallet>(command.FromWalletId);
        if (from is null)
            return new ProblemDetails { Detail = "Source wallet not found", Status = 404 };
        if (from.Balance < command.Amount)
            return new ProblemDetails { Detail = "Insufficient funds", Status = 400 };

        var to = await session.LoadAsync<Wallet>(command.ToWalletId);
        if (to is null)
            return new ProblemDetails { Detail = "Destination wallet not found", Status = 404 };
        if (from.Currency != to.Currency)
            return new ProblemDetails { Detail = "Currency mismatch", Status = 400 };

        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/wallets/transfer")]
    public static async Task<(Wallet, FundsDeducted, FundsAdded)> Post(
        TransferFunds command,
        IDocumentSession session,
        CancellationToken ct)
    {
        var from = await session.LoadAsync<Wallet>(command.FromWalletId, ct);
        var to = await session.LoadAsync<Wallet>(command.ToWalletId, ct);

        var transferId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        from!.Balance -= command.Amount;
        from.Transfers.Add(new Transfer { Id = transferId, Name = "transfer-out", Type = "Outgoing", Amount = command.Amount, CreatedAt = now });

        to!.Balance += command.Amount;
        to.Transfers.Add(new Transfer { Id = transferId, Name = "transfer-in", Type = "Incoming", Amount = command.Amount, CreatedAt = now });

        session.Store(from);
        session.Store(to);

        return (from,
            new FundsDeducted(from.Id, from.OwnerId, from.Currency, command.Amount),
            new FundsAdded(to.Id, to.OwnerId, to.Currency, command.Amount));
    }
}
