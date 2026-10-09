using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Scheduling;

public class ScheduledMessageReceiver
{
    public readonly IList<ScheduledMessage> ReceivedMessages = new List<ScheduledMessage>();

    public readonly TaskCompletionSource<ScheduledMessage> Source = new();

    public Task<ScheduledMessage> Received => Source.Task;
}

public abstract class ScheduledJobCompliance: IAsyncLifetime
{
    private readonly ScheduledMessageReceiver theReceiver = new();
    private IHost theHost = null!;
    
    public abstract void ConfigurePersistence(WolverineOptions opts);
    
    public async ValueTask InitializeAsync()
    {
        theHost = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                opts.Services.AddSingleton(theReceiver);

                opts.Publish(x => x.MessagesFromAssemblyContaining<ScheduledMessageReceiver>()
                    .ToLocalQueue("incoming").UseDurableInbox());

                opts.Discovery.DisableConventionalDiscovery().IncludeType<ScheduledMessageCatcher>();

                ConfigurePersistence(opts);

            })
            .StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
    
    protected ValueTask ScheduleMessage(int id, int seconds)
    {
        return theHost.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new ScheduledMessage { Id = id }, seconds.Seconds());
    }

    protected ValueTask ScheduleSendMessage(int id, int seconds)
    {
        return new Wolverine.Runtime.MessageBus(theHost.GetRuntime())
            .ScheduleAsync(new ScheduledMessage { Id = id }, seconds.Seconds());
    }

    protected int ReceivedMessageCount()
    {
        return theReceiver.ReceivedMessages.Count;
    }

    protected Task AfterReceivingMessages()
    {
        return theReceiver.Received;
    }

    protected int TheIdOfTheOnlyReceivedMessageShouldBe()
    {
        return theReceiver.ReceivedMessages.Single().Id;
    }

    protected async Task<int> PersistedScheduledCount()
    {
        var counts = await theHost.Services.GetRequiredService<IMessageStore>().Admin.FetchCountsAsync();
        return counts.Scheduled;
    }

    protected async Task PersistedScheduledCountShouldBe(int expected)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var count = await PersistedScheduledCount();
        while (stopwatch.Elapsed < 5.Seconds() && count != expected)
        {
            await Task.Delay(100.Milliseconds());
            count = await PersistedScheduledCount();
        }

        count.ShouldBe(expected);
    }

    [Fact]
    public async Task execute_scheduled_job()
    {
        await ScheduleSendMessage(1, 7200);
        await ScheduleSendMessage(2, 5);
        await ScheduleSendMessage(3, 7200);

        ReceivedMessageCount().ShouldBe(0);

        // Both waits are bounded. Unbounded, a scheduled message that never fires held a CI job until its
        // 20 minute cap cancelled it, with nothing in the log to say why (CICosmosDb, 2026-10-09). Bounded,
        // the failure names the phase it stuck in and dumps what the node knows.
        var received = AfterReceivingMessages();
        if (await Task.WhenAny(received, Task.Delay(60.Seconds())) != received)
        {
            throw new TimeoutException(
                $"The 5 second scheduled message was never received within 60 seconds.\n{await describeNodeStateAsync()}");
        }

        //TheIdOfTheOnlyReceivedMessageShouldBe().ShouldBe(2);

        var stopwatch = Stopwatch.StartNew();
        while (await PersistedScheduledCount() != 2)
        {
            if (stopwatch.Elapsed > 60.Seconds())
            {
                throw new TimeoutException(
                    $"The persisted scheduled count never settled at 2 within 60 seconds of the message arriving.\n{await describeNodeStateAsync()}");
            }

            await Task.Delay(250.Milliseconds());
        }

        (await PersistedScheduledCount()).ShouldBe(2);
    }

    private async Task<string> describeNodeStateAsync()
    {
        var runtime = theHost.GetRuntime();
        var lines = new List<string>
        {
            $"received: [{string.Join(", ", theReceiver.ReceivedMessages.Select(x => x.Id))}]",
            $"this node: {runtime.Options.UniqueNodeId} (number {runtime.Options.Durability.AssignedNodeNumber}), mode {runtime.Options.Durability.Mode}, leader: {runtime.NodeController?.IsLeader}",
            $"running here: [{string.Join(", ", runtime.Agents.AllRunningAgentUris().Select(x => x.ToString()))}]"
        };

        try
        {
            var counts = await runtime.Storage.Admin.FetchCountsAsync();
            lines.Add($"envelope counts: incoming {counts.Incoming}, scheduled {counts.Scheduled}, handled {counts.Handled}, outgoing {counts.Outgoing}, dead letter {counts.DeadLetter}");
        }
        catch (Exception e)
        {
            lines.Add($"envelope counts unavailable: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            var state = await runtime.Storage.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);
            lines.Add($"persisted nodes ({state.Nodes.Count}):");
            foreach (var node in state.Nodes)
            {
                var age = DateTimeOffset.UtcNow - node.LastHealthCheck;
                lines.Add($"  {node.NodeId} number {node.AssignedNodeNumber}, last health check {age.TotalSeconds:F0}s ago, running [{string.Join(", ", node.ActiveAgents.Select(x => x.ToString()))}]");
            }
        }
        catch (Exception e)
        {
            lines.Add($"node state unavailable: {e.GetType().Name}: {e.Message}");
        }

        return string.Join("\n", lines);
    }
}

public class ScheduledMessageCatcher
{
    private readonly ScheduledMessageReceiver _receiver;

    public ScheduledMessageCatcher(ScheduledMessageReceiver receiver)
    {
        _receiver = receiver;
    }

    public void Consume(ScheduledMessage message)
    {
        if (!_receiver.Source.Task.IsCompleted)
        {
            _receiver.Source.SetResult(message);
        }

        _receiver.ReceivedMessages.Add(message);
    }
}