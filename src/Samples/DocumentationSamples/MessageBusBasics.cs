using Baseline.Dates;
using Wolverine;

namespace DocumentationSamples;

public class MessageBusBasics
{
    #region sample_message_bus_basics

    public static async Task use_message_bus(IMessageBus bus)
    {
        // Execute this command message right now! And wait until
        // it's completed or acknowledged
        await bus.InvokeAsync(new DebitAccount(100));
        
        // Execute this message right now, but wait for the declared response
        var status = await bus.InvokeAsync<AccountStatus>(new DebitAccount(250));
        
        // Send the message expecting there to be at least one subscriber to be executed later, but
        // don't wait around
        await bus.SendAsync(new DebitAccount(250));
        
        // Or instead, publish it to any interested subscribers, 
        // but don't worry about it if there are actually any subscribers
        // This is probably best for raising event messages
        await bus.PublishAsync(new DebitAccount(300));
        
        // Send a message to be sent or executed at a specific time
        await bus.ScheduleAsync(new DebitAccount(100), DateTimeOffset.UtcNow.AddDays(1));
        
        // Or do the same, but this time express the time as a delay
        await bus.ScheduleAsync(new DebitAccount(225.25L), 1.Days());
    }

    #endregion
}

public record DebitAccount(float Amount);
public record AccountStatus(float Status);