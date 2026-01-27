using Marten;
using Marten.Events;
using Shouldly;
using WolverineWebApi.Accounts;

namespace Wolverine.Http.Tests.Marten;

public class working_against_multiple_streams : IntegrationContext
{
    public working_against_multiple_streams(AppFixture fixture) : base(fixture)
    {
    }

    private async Task<Guid> createAccount(double amount)
    {
        var created = new AccountCreated(amount);
        using var session = Host.DocumentStore().LightweightSession();
        var id = session.Events.StartStream<Account>(created).Id;
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<double> fetchAmount(Guid id)
    {
        using var session = Host.DocumentStore().LightweightSession();
        var account = await session.Events.FetchLatest<Account>(id);
        return account.Amount;
    }

    [Fact]
    public async Task happy_path_found_both_accounts_append_to_both()
    {
        var from = await createAccount(1000);
        var to = await createAccount(100);

        await Scenario(x =>
        {
            x.Post.Json(new TransferMoney(from, to, 150)).ToUrl("/accounts/transfer");
            x.StatusCodeShouldBe(204);
        });
        
        (await fetchAmount(from)).ShouldBe(850);
        (await fetchAmount(to)).ShouldBe(250);
    }

    [Fact]
    public async Task reject_when_the_from_does_not_have_enough_funds()
    {
        var from = await createAccount(1000);
        var to = await createAccount(100);

        await Scenario(x =>
        {
            x.Post.Json(new TransferMoney(from, to, 2000)).ToUrl("/accounts/transfer");
            x.StatusCodeShouldBe(204);
        });
        
        (await fetchAmount(from)).ShouldBe(1000);
        (await fetchAmount(to)).ShouldBe(100);
    }

    [Fact]
    public async Task return_404_when_first_account_does_not_exist()
    {
        //var from = await createAccount(1000);
        var to = await createAccount(100);

        await Scenario(x =>
        {
            x.Post.Json(new TransferMoney(Guid.NewGuid(), to, 2000)).ToUrl("/accounts/transfer");
            x.StatusCodeShouldBe(404);
        });
    }
    
    [Fact]
    public async Task return_404_when_second_account_does_not_exist()
    {
        var from = await createAccount(1000);
        //var to = await createAccount(100);

        await Scenario(x =>
        {
            x.Post.Json(new TransferMoney(from, Guid.NewGuid(), 2000)).ToUrl("/accounts/transfer");
            x.StatusCodeShouldBe(404);
        });
    }
    
    [Fact]
    public async Task happy_path_found_both_accounts_append_to_both_with_exclusive_lock()
    {
        var from = await createAccount(1000);
        var to = await createAccount(100);

        await Scenario(x =>
        {
            x.Post.Json(new TransferMoney(from, to, 150)).ToUrl("/accounts/transfer2");
            x.StatusCodeShouldBe(204);
        });
        
        (await fetchAmount(from)).ShouldBe(850);
        (await fetchAmount(to)).ShouldBe(250);
    }
}

#region sample_when_transfering_money

public class when_transfering_money
{
    [Fact]
    public void happy_path_have_enough_funds()
    {
        // StubEventStream<T> is a type that was recently added to Marten
        // specifically to facilitate testing logic like this
        var fromAccount = new StubEventStream<Account>(new Account { Amount = 1000 })
        {
            Id = Guid.NewGuid()
        };
        
        var toAccount = new StubEventStream<Account>(new Account { Amount = 100})
        {
            Id = Guid.NewGuid()
        };
        
        TransferMoneyHandler.Handle(new TransferMoney(fromAccount.Id, toAccount.Id, 100), fromAccount, toAccount);

        // Now check the events we expected to be appended
        fromAccount.Events.Single().Data.ShouldBeOfType<Withdrawn>().Amount.ShouldBe(100);
        toAccount.Events.Single().Data.ShouldBeOfType<Debited>().Amount.ShouldBe(100);
    }
}

#endregion