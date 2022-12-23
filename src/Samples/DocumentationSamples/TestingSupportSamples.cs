using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace DocumentationSamples;

public class TestingSupportSamples
{
    public static async Task stub_all_external_transports()
    {
        #region sample_conditionally_disable_transports

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // Other configuration...

                // IF the environment is "Testing", turn off all external transports
                if (context.HostingEnvironment.EnvironmentName.EqualsIgnoreCase("Testing"))
                {
                    opts.StubAllExternalTransports();
                }
            }).StartAsync();

        #endregion
    }
}


public static class AccountHandler
{
    #region sample_AccountHandler_for_testing_examples

    [Transactional] 
    public static IEnumerable<object> Handle(
        DebitAccount command, 
        Account account, 
        IDocumentSession session)
    {
        account.Balance -= command.Amount;
     
        // This just marks the account as changed, but
        // doesn't actually commit changes to the database
        // yet. That actually matters as I hopefully explain
        session.Store(account);
 
        // Conditionally trigger other, cascading messages
        if (account.Balance > 0 && account.Balance < account.MinimumThreshold)
        {
            yield return new LowBalanceDetected(account.Id);
        }
        else if (account.Balance < 0)
        {
            yield return new AccountOverdrawn(account.Id);
         
            // Give the customer 10 days to deal with the overdrawn account
            yield return new EnforceAccountOverdrawnDeadline(account.Id);
        }

        yield return new AccountUpdated(account.Id, account.Balance);
    }

    #endregion
}

public class AccountHandlerTests
{
    #region sample_handle_a_debit_that_makes_the_account_have_a_low_balance

    [Fact]
    public void handle_a_debit_that_makes_the_account_have_a_low_balance()
    {
        var account = new Account
        {
            Balance = 1000,
            MinimumThreshold = 200,
            Id = 1111
        };

        // Let's otherwise ignore this for now, but this is using NSubstitute
        var session = Substitute.For<IDocumentSession>();

        var message = new DebitAccount(account.Id, 801);
        var messages = AccountHandler.Handle(message, account, session);
        
        // Now, verify that the only the expected messages are published:
        
        // Exactly one message of type LowBalanceDetected
        messages
            .ShouldHaveMessageOfType<LowBalanceDetected>()
            .AccountId.ShouldBe(account.Id);
        
        // Assert that there are no messages of type AccountOverdrawn
        messages.ShouldHaveNoMessageOfType<AccountOverdrawn>();
    }

    #endregion


    [Fact]

    #region sample_using_tracked_session

    public async Task using_tracked_sessions()
    {
        // The point here is just that you somehow have
        // an IHost for your application
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var debitAccount = new DebitAccount(111, 300);
        var session = await host.InvokeMessageAndWaitAsync(debitAccount);

        var overdrawn = session.Sent.SingleMessage<AccountOverdrawn>();
        overdrawn.AccountId.ShouldBe(debitAccount.AccountId);
    }

    #endregion

    #region sample_advanced_tracked_session_usage

    public async Task using_tracked_sessions_advanced(IHost otherWolverineSystem)
    {
        // The point here is just that you somehow have
        // an IHost for your application
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var debitAccount = new DebitAccount(111, 300);
        var session = await host
                
            // Start defining a tracked session 
            .TrackActivity()
            
            // Override the timeout period for longer tests
            .Timeout(1.Minutes())
            
            // Be careful with this one! This makes Wolverine wait on some indication
            // that messages sent externally are completed
            .IncludeExternalTransports()
            
            // Make the tracked session span across an IHost for another process
            // May not be super useful to the average user, but it's been crucial
            // to test Wolverine itself
            .AlsoTrack(otherWolverineSystem)
            
            // SERIOUSLY be careful with this
            .DoNotAssertTimeout()
            
            // This is actually helpful if you are testing for error handling 
            // functionality in your system
            .DoNotAssertOnExceptionsDetected()
            
            // Again, this is testing against processes, with another IHost
            .WaitForMessageToBeReceivedAt<LowBalanceDetected>(otherWolverineSystem)
            
            // There are many other options as well
            .InvokeMessageAndWaitAsync(debitAccount);

        var overdrawn = session.Sent.SingleMessage<AccountOverdrawn>();
        overdrawn.AccountId.ShouldBe(debitAccount.AccountId);
    }

    #endregion
}




// The attribute directs Wolverine to send this message with 
// a "deliver within 5 seconds, or discard" directive
[DeliverWithin(5)]
public record AccountUpdated(long AccountId, decimal Balance);

public record LowBalanceDetected(long AccountId) : IAccountCommand;

public record EnforceAccountOverdrawnDeadline(long AccountId) : TimeoutMessage(10.Days()), IAccountCommand;

public class Account
{
    public long Id { get; set; }
    public decimal Balance { get; set; }
    public decimal MinimumThreshold { get; set; }
}

public interface IAccountCommand
{
    long AccountId { get; }
}