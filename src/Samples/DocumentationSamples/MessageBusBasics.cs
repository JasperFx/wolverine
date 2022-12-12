using Wolverine;
using JasperFx.Core;

namespace DocumentationSamples;

public class MessageBusBasics
{
    #region sample_message_bus_basics

    public static async Task use_message_bus(IMessageBus bus)
    {
        // Execute this command message right now! And wait until
        // it's completed or acknowledged
        await bus.InvokeAsync(new DebitAccount(1111, 100));
        
        // Execute this message right now, but wait for the declared response
        var status = await bus.InvokeAsync<AccountStatus>(new DebitAccount(1111, 250));
        
        // Send the message expecting there to be at least one subscriber to be executed later, but
        // don't wait around
        await bus.SendAsync(new DebitAccount(1111, 250));
        
        // Or instead, publish it to any interested subscribers, 
        // but don't worry about it if there are actually any subscribers
        // This is probably best for raising event messages
        await bus.PublishAsync(new DebitAccount(1111, 300));
        
        // Send a message to be sent or executed at a specific time
        await bus.ScheduleAsync(new DebitAccount(1111, 100), DateTimeOffset.UtcNow.AddDays(1));
        
        // Or do the same, but this time express the time as a delay
        await bus.ScheduleAsync(new DebitAccount(1111, 225), 1.Days());
    }

    #endregion

    #region sample_invoke_debit_account

    public async Task invoke_debit_account(IMessageBus bus)
    {
        // Debit $250 from the account #2222
        await bus.InvokeAsync(new DebitAccount(2222, 250));
    }

    #endregion

    #region sample_using_invoke_with_response_type

    public async Task invoke_math_operations(IMessageBus bus)
    {
        var results = await bus.InvokeAsync<Results>(new Numbers(3, 4));
    }

    #endregion
}

#region sample_DebitAccountHandler

public static class DebitAccountHandler
{
    public static void Handle(DebitAccount account)
    {
        Console.WriteLine($"I'm supposed to debit {account.Amount} from account {account.AccountId}");
    }
}

#endregion

#region sample_DebutAccount_command

// A "command" message
public record DebitAccount(long AccountId, float Amount);

// An "event" message
public record AccountOverdrawn(long AccountId);

#endregion
public record AccountStatus(long AccountId, float Status);

#region sample_numbers_and_results_for_request_response

public record Numbers(int X, int Y);
public record Results(int Sum, int Product);

public static class NumbersHandler
{
    public static Results Handle(Numbers numbers)
    {
        return new Results(numbers.X + numbers.Y, numbers.X * numbers.Y);
    }
}

#endregion