using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

// This is really to test core Wolverine behavior
public class missing_handler_behavior
{
    

    [Fact]
    public async Task no_registered_missing_handlers_for_default_behavior()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();
        
        host.Services.GetServices<IMissingHandler>().ShouldBeEmpty();
    }

    [Fact]
    public async Task no_registered_missing_handlers_move_to_dead_letter_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
            })
            .StartAsync();
        
        host.Services.GetServices<IMissingHandler>().ShouldContain(x => x is MoveUnknownMessageToDeadLetterQueue);
    }

    [Fact]
    public async Task reentrant_config_for_overloads()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
                opts.UnknownMessageBehavior = UnknownMessageBehavior.LogOnly;
            })
            .StartAsync();
        
        host.Services.GetServices<IMissingHandler>().ShouldBeEmpty();
    }
    
    [Fact]
    public async Task reentrant_config_for_overloads_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
                opts.UnknownMessageBehavior = UnknownMessageBehavior.LogOnly;
                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
            })
            .StartAsync();
        
        host.Services.GetServices<IMissingHandler>().ShouldContain(x => x is MoveUnknownMessageToDeadLetterQueue);
    }
    
    
}

