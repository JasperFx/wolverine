using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using JasperFx.Events;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Tracking;

// IsCompleted() used to check a snapshot of the envelope histories, so a handler that sent its cascading
// messages and finished while the check ran was counted as complete without them. The race only shows up
// under CPU pressure, so this loops many tracked sessions to give it a chance to happen
public class tracked_session_waits_for_cascades_of_durable_local_queues : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FanOutRootHandler))
                    .IncludeType(typeof(FanOutMiddleHandler))
                    .IncludeType(typeof(FanOutLeafHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "tracked_fan_out";
                    m.Listeners.Add(new PublishRootOnTriggerSave());
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task every_cascaded_leaf_has_run_when_execute_and_wait_returns()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();

        for (var i = 0; i < 500; i++)
        {
            var runId = Guid.NewGuid();

            var session = await _host.ExecuteAndWaitAsync(async _ =>
            {
                await using var marten = store.LightweightSession();
                marten.Store(new FanOutTrigger { Id = runId });
                await marten.SaveChangesAsync();
            });

            var incomplete = session.AllRecordsInOrder()
                .GroupBy(x => x.Envelope!.Id)
                .Where(g => g.All(r => r.MessageEventType != MessageEventType.MessageSucceeded))
                .Select(g => $"{g.First().Envelope!.MessageType}: {string.Join(", ", g.Select(r => r.MessageEventType))}")
                .ToArray();

            incomplete.ShouldBeEmpty($"iteration {i}: session {session.Status} with unfinished envelopes");
            FanOutLeafHandler.RunsOf(runId).ShouldBe(4, $"iteration {i}");
        }
    }
}

public class FanOutTrigger
{
    public Guid Id { get; set; }
}

public class FanOutWork
{
    public Guid Id { get; set; }
}

public record FanOutRoot(Guid RunId);

public record FanOutMiddle(Guid RunId, int Branch);

public record FanOutLeaf(Guid RunId, int Branch, int Leaf);

// Publishes from inside SaveChanges through Marten's message sink, as an application's session listener would
public class PublishRootOnTriggerSave : DocumentSessionListenerBase
{
    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var triggers = session.PendingChanges.AllChangedFor<FanOutTrigger>().ToArray();
        if (triggers.Length == 0) return;

        var sink = await ((IStorageOperations)session).GetOrStartMessageSink();
        foreach (var trigger in triggers)
        {
            await sink.PublishAsync(new FanOutRoot(trigger.Id), session.TenantId);
        }
    }
}

public static class FanOutRootHandler
{
    public static OutgoingMessages Handle(FanOutRoot message, IDocumentSession session)
    {
        session.Store(new FanOutWork { Id = Guid.NewGuid() });
        return [new FanOutMiddle(message.RunId, 0), new FanOutMiddle(message.RunId, 1)];
    }
}

public static class FanOutMiddleHandler
{
    public static OutgoingMessages Handle(FanOutMiddle message, IDocumentSession session)
    {
        session.Store(new FanOutWork { Id = Guid.NewGuid() });
        return [new FanOutLeaf(message.RunId, message.Branch, 0), new FanOutLeaf(message.RunId, message.Branch, 1)];
    }
}

public static class FanOutLeafHandler
{
    private static readonly ConcurrentDictionary<Guid, int> _runs = new();

    public static int RunsOf(Guid runId) => _runs.GetValueOrDefault(runId);

    public static async Task Handle(FanOutLeaf message, IDocumentSession session)
    {
        await Task.Delay(20);
        _runs.AddOrUpdate(message.RunId, 1, (_, count) => count + 1);
    }
}
