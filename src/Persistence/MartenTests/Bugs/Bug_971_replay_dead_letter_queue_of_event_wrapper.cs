using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Events;
using Marten.Events.Daemon.Resiliency;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_971_replay_dead_letter_queue_of_event_wrapper
{
    [Fact]
    public async Task can_replay_dead_letter_event()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = GetType().Assembly;
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "dead_letter_queue";
                        m.DisableNpgsqlLogging = true;
                    })
                    .AddAsyncDaemon(DaemonMode.Solo)
                    .IntegrateWithWolverine()
                    .PublishEventsToWolverine("MaybeErrors", r => r.PublishEvent<ErrorCausingEvent>());
                
            }).StartAsync();

        var runtime = host.GetRuntime();
        await runtime.Storage.Admin.RebuildAsync();
        
        ErrorCausingEventHandler.ShouldThrow = true;

        using (var session = host.DocumentStore().LightweightSession())
        {
            session.Events.StartStream(new ErrorCausingEvent());
            await session.SaveChangesAsync();
        }

        await host.WaitForNonStaleProjectionDataAsync(60.Seconds());

        Func<IMessageContext, Task> tryReplayEventMessage = async _ =>
        {
            bool hasReplayed = false;

            var count = 0;
            while (true)
            {
                count++;
                var messages =
                    await runtime.Storage.DeadLetters.QueryDeadLetterEnvelopesAsync(
                        new DeadLetterEnvelopeQueryParameters());

                if (hasReplayed && !messages.DeadLetterEnvelopes.Any())
                {
                    break; // we're done!
                }

                if (messages.DeadLetterEnvelopes.Any(x => !x.Replayable))
                {
                    ErrorCausingEventHandler.ShouldThrow = false;

                    await runtime.Storage.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(messages
                        .DeadLetterEnvelopes.Select(x => x.Id).ToArray());

                    hasReplayed = true;
                }

                await Task.Delay(250.Milliseconds());

                if (count > 1000) throw new TimeoutException("Never found dead letter queue messages");
            }
        };

        var tracked = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(15.Seconds())
            .WaitForMessageToBeReceivedAt<IEvent<ErrorCausingEvent>>(host)
            .ExecuteAndWaitAsync(tryReplayEventMessage);

        tracked.MessageSucceeded.SingleMessage<IEvent<ErrorCausingEvent>>()
            .ShouldNotBeNull();


        
    }
}



public class ErrorCausingEvent
{
    
}

public static class ErrorCausingEventHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<BadImageFormatException>()
            .MoveToErrorQueue();
    }
    
    public static bool ShouldThrow { get; set; } = true;

    // public static void Handle(Event<ErrorCausingEvent> e)
    // {
    //     
    // }

    public static void Handle(IEvent<ErrorCausingEvent> e)
    {
        if (ShouldThrow) throw new BadImageFormatException("boom");
        
        Debug.WriteLine("All good");
    }
}