using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_1594_ReplayDeadLetterQueue
{
    private readonly ITestOutputHelper _output;
    public Bug_1594_ReplayDeadLetterQueue(ITestOutputHelper output) { _output = output; ReplayTestHandler.Output = output; }

    [Theory]
    [InlineData(EndpointMode.Inline)] 
    [InlineData(EndpointMode.Durable)] 
    [InlineData(EndpointMode.BufferedInMemory)] 
    public async Task can_replay_dead_letter_message(EndpointMode mode)
    {
        var queueName = $"replay-dlq-{Guid.NewGuid()}";
        var connectionString = Servers.SqlServerConnectionString;

        // Reset handler state
        ReplayTestHandler.Reset();
        ReplayTestHandler.FailFirst = true;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<ReplayTestMessage>().ToRabbitQueue(queueName);
                opts.ListenToRabbitQueue(queueName, q => q.As<Endpoint>().Mode = mode);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await host.ResetResourceState();
        
        await host.MessageBus().PublishAsync(new ReplayTestMessage());
        
        await Task.Delay(1000);

        var messageStore = host.Services.GetRequiredService<IMessageStore>();
        var deadLetterQuery = new DeadLetterEnvelopeQuery { PageSize = 10 };
        var sw = Stopwatch.StartNew();
        Guid? deadLetterId = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var deadLetterResults = await messageStore.DeadLetters.QueryAsync(deadLetterQuery, CancellationToken.None);
            if (deadLetterResults.Envelopes.Any())
            {
                deadLetterId = deadLetterResults.Envelopes.First().Id;
                break;
            }
            await Task.Delay(100);
        }
        
        deadLetterId.ShouldNotBeNull("Message should be in DLQ after failure");

        // Log state before replay
        var beforeReplay = await messageStore.DeadLetters.QueryAsync(deadLetterQuery, CancellationToken.None);
        var beforeIncoming = await messageStore.Admin.AllIncomingAsync();
        _output.WriteLine($"[BEFORE REPLAY] DLQ: {beforeReplay.Envelopes.Count}, Incoming: {beforeIncoming.Count}");

        // Force handler to succeed on replay (mimic Marten test)
        ReplayTestHandler.FailFirst = false;

        var tracked = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<ReplayTestMessage>(host)
            .ExecuteAndWaitAsync((IMessageContext _) => messageStore.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(new[] { deadLetterId.Value }));

        // Log state after replay
        var afterReplay = await messageStore.DeadLetters.QueryAsync(deadLetterQuery, CancellationToken.None);
        var afterIncoming = await messageStore.Admin.AllIncomingAsync();
        foreach (var env in afterIncoming)
        {
            _output.WriteLine($"[INCOMING] Id: {env.Id}, Status: {env.Status}, OwnerId: {env.OwnerId}, ScheduledTime: {env.ScheduledTime}, Attempts: {env.Attempts}, ReceivedAt: {env.Destination}");
        }

        // Assert using the tracking result, mimicking the Marten test
        tracked.MessageSucceeded.SingleMessage<ReplayTestMessage>()
            .ShouldNotBeNull("ReplayTestMessage should be successfully processed after replay");
        afterReplay.Envelopes.Any(dl => dl.Id == deadLetterId).ShouldBeFalse("Message should be removed from DLQ after successful replay (this should work for both durable and non-durable queues)");
        afterIncoming.Any(env => env.Status == EnvelopeStatus.Incoming && env.Id == deadLetterId).ShouldBeFalse("Message should not remain in Incoming after successful processing");
    }
}

public record ReplayTestMessage;

public static class ReplayTestHandler
{
    public static bool WasCalled = false;
    public static bool FailFirst = true;
    public static ITestOutputHelper? Output;
    public static void Reset() { WasCalled = false; FailFirst = true; }
    public static void Handle(ReplayTestMessage command)
    {
        var msg = $"[HANDLER] Called. FailFirst={FailFirst}";
        if (Output != null) Output.WriteLine(msg); else Console.WriteLine(msg);
        if (FailFirst)
        {
            FailFirst = false;
            throw new DivideByZeroException("Boom.");
        }
        WasCalled = true;
    }
} 