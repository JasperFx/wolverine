using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Azure.Cosmos;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.CosmosDb.Internals.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace CosmosDbTests;

[Collection("cosmosdb")]
public class durability_recovery_capacity(AppFixture fixture)
{
    [Theory]
    [InlineData(9, 0)] // One slot of headroom
    [InlineData(10, 0)] // Negative allowance
    [InlineData(6, 4)] // Exact fit is allowed, matching relational recovery
    [InlineData(7, 2)] // Oversized batches retain one slot of headroom
    [InlineData(4, 4)] // RecoveryBatchSize remains the upper bound
    public async Task recovery_respects_capacity_and_resumes(int depth, int expected)
    {
        await fixture.ClearAll();
        var gate = new RecoveryCapacityGate();
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Services.AddSingleton(fixture.Client);
            opts.Services.AddSingleton(gate);
            opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.RecoveryBatchSize = 4;
            opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromHours(1);
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RecoveryCapacityHandler));
            opts.LocalQueue("capacity-a").UseDurableInbox().Sequential();
            opts.LocalQueue("capacity-b").UseDurableInbox().Sequential();
        }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var inbox = (CosmosDbMessageStore)host.Services.GetRequiredService<IMessageStore>();
        var a = new Uri("local://capacity-a/");
        var b = new Uri("local://capacity-b/");
        var circuit = runtime.Endpoints.FindListenerCircuit(a)!;
        try
        {
            // A running handler plus queued messages are held until all ownership assertions finish.
            for (var i = 0; i < depth; i++)
                await host.MessageBus().EndpointFor(a).SendAsync(new RecoveryCapacityMessage(true));
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await waitUntil(() => circuit.QueueCount == depth);
            circuit.Endpoint.BufferingLimits = new BufferingLimits(10, 5);
            circuit.Status.ShouldBe(ListeningStatus.Accepting);

            var documents = new List<IncomingMessage>();
            for (var i = 0; i < 8; i++)
            {
                var envelope = new Envelope
                {
                    Destination = a, Status = EnvelopeStatus.Incoming,
                    OwnerId = TransportConstants.AnyNode,
                    MessageType = typeof(RecoveryCapacityMessage).FullName!,
                    ContentType = "application/json",
                    Data = JsonSerializer.SerializeToUtf8Bytes(new RecoveryCapacityMessage(true))
                };
                await inbox.StoreIncomingAsync(envelope);
                documents.Add(new IncomingMessage(envelope, inbox));
            }
            var available = new Envelope
            {
                Destination = b, Status = EnvelopeStatus.Incoming, OwnerId = TransportConstants.AnyNode,
                MessageType = typeof(RecoveryCapacityMessage).FullName!, ContentType = "application/json",
                Data = JsonSerializer.SerializeToUtf8Bytes(new RecoveryCapacityMessage(false))
            };
            await inbox.StoreIncomingAsync(available);

            async Task sweep()
            {
                var agent = inbox.BuildAgent(runtime);
                var method = typeof(CosmosDbDurabilityAgent).GetMethod("tryRecoverIncomingMessages",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await ((Task)method.Invoke(agent, null)!).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

                // GH-5587. The sweep starts each listener's recovery on its own lane and returns
                var lanes = typeof(CosmosDbDurabilityAgent).GetMethod("WaitForRecoveryLanesAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await ((Task)lanes.Invoke(agent, null)!).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }

            await sweep();
            await gate.Available.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            circuit.Status.ShouldBe(ListeningStatus.Accepting);
            var rows = new List<IncomingMessage>();
            foreach (var document in documents)
            {
                var response = await fixture.Container.ReadItemAsync<IncomingMessage>(document.Id,
                    new PartitionKey(document.PartitionKey), cancellationToken: TestContext.Current.CancellationToken);
                rows.Add(response.Resource);
            }
            rows.Count(x => x.OwnerId == runtime.DurabilitySettings.AssignedNodeNumber).ShouldBe(expected);
            rows.Count(x => x.OwnerId == TransportConstants.AnyNode).ShouldBe(8 - expected);
            rows.ShouldAllBe(x => x.Status == EnvelopeStatus.Incoming && x.ReceivedAt == a.ToString());

            gate.Release.TrySetResult();
            await waitUntil(() => circuit.QueueCount == 0);
            await sweep();
            await waitUntil(() => circuit.QueueCount == 0);
            await sweep();
            await waitUntil(() => Volatile.Read(ref gate.Completed) == depth + 8);
        }
        finally
        {
            gate.Release.TrySetResult();
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

public record RecoveryCapacityMessage(bool Gated);
public class RecoveryCapacityGate
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Available { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Completed;
}
public static class RecoveryCapacityHandler
{
    public static async Task Handle(RecoveryCapacityMessage message, RecoveryCapacityGate gate)
    {
        if (!message.Gated)
        {
            gate.Available.TrySetResult();
            return;
        }
        gate.Started.TrySetResult();
        await gate.Release.Task;
        Interlocked.Increment(ref gate.Completed);
    }
}
