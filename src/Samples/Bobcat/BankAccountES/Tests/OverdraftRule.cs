using Wolverine.Http;

namespace BankAccountES.Tests;

/// <summary>
/// The overdraft rule at the finest grain that tells you something: <see cref="WithdrawFundsEndpoint.Validate" />
/// is a pure function of the command and the account, so it needs no host, no store and no Bobcat. The HTTP
/// specification beside it (<c>withdrawing_more_than_the_balance_leaves_the_account_untouched</c>) proves the
/// wiring once; these prove the rule. The "Test Automation Strategy Recommendations" tutorial pulls them in.
/// </summary>
public class OverdraftRule
{
    // #region overdraft-rule
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused()
    {
        var account = new Account { Balance = 100m };

        var problems = WithdrawFundsEndpoint.Validate(new WithdrawFunds(Guid.NewGuid(), 200m), account);

        Assert.Equal(400, problems.Status);
    }

    [Fact]
    public void a_withdrawal_of_the_whole_balance_is_allowed()
    {
        var account = new Account { Balance = 100m };

        var problems = WithdrawFundsEndpoint.Validate(new WithdrawFunds(Guid.NewGuid(), 100m), account);

        Assert.Same(WolverineContinue.NoProblems, problems);
    }
    // #endregion overdraft-rule
}
