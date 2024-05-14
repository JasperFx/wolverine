namespace BankingService;

public class AccountEndpoint
{
    public Task<AccountStatus> PostDebit(DebitAccount command, IAccountService service)
    {
        return service.DebitAsync(command.AccountId, command.Amount);
    }
}

public record AccountStatus(int AccountId, decimal Amount);
public record DebitAccount(int AccountId, decimal Amount);

public class DebitAccountHandler
{
    public static Task Handle(DebitAccount command, IAccountService service)
    {
        return service.DebitAsync(command.AccountId, command.Amount);
    }
}

public interface IAccountService
{
    Task<AccountStatus> DebitAsync(int accountId, decimal amount);
}

public class RealAccountService : IAccountService
{
    public Task<AccountStatus> DebitAsync(int accountId, decimal amount)
    {
        throw new NotImplementedException("Not here yet!");
    }
}