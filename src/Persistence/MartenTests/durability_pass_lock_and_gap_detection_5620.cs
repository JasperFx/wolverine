using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using JasperFx.Core.Reflection;

namespace MartenTests;

/// <summary>
/// GH-5620. With <see cref="DurabilitySettings.UseTransactionScopedDurabilityPassLock" /> the durability pass holds its
/// advisory lock in a session that is idle in an open transaction for the whole pass. Marten's gap detection counted
/// that session as a possible reserver of any event sequence gap first seen during the pass, so a pass step that
/// catches asynchronous projections up waited on its own pass until it timed out. IntegrateWithWolverine() now
/// registers the lock with Marten's gap detection (marten#5791), which then ignores the lock's holder.
/// </summary>
public class durability_pass_lock_and_gap_detection_5620 : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "gap_probe_5620";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.DropSchemaAsync(SchemaName, Token);
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.UseTransactionScopedDurabilityPassLock = true;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;

                        m.Projections.StaleSequenceThreshold = 500.Milliseconds();
                        m.Projections.Snapshot<GapCounter>(SnapshotLifecycle.Async);
                    })
                    .IntegrateWithWolverine();

                // A pass step that catches the asynchronous projections up, as a serverless application would
                opts.Services.AddSingleton<IDurabilityPassStep, CatchUpAsyncProjections>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task appendAsync(Guid streamId)
    {
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.Append(streamId, new GapCounted());
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public void the_pass_lock_of_the_message_store_is_registered_with_gap_detection()
    {
        var messageStore = theHost.GetRuntime().Storage.ShouldBeOfType<PostgresqlMessageStore>();

        theHost.DocumentStore().As<DocumentStore>().Options.Projections.IgnoredAdvisoryLockKeysForGapDetection
            .ShouldContain(messageStore.DurabilityPassLockId);

        messageStore.DurabilityPassLockIgnoredByGapDetection.ShouldBeTrue();
    }

    [Fact]
    public async Task a_pass_step_catches_projections_up_past_a_dead_gap()
    {
        // One event, two sequence numbers nobody uses, another event: a gap that exists before any daemon polls
        var streamId = Guid.NewGuid();
        await appendAsync(streamId);

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.CreateCommand(
                    $"select nextval('{SchemaName}.mt_events_sequence'); select nextval('{SchemaName}.mt_events_sequence')")
                .ExecuteNonQueryAsync(Token);
        }

        await appendAsync(streamId);

        var report = await theHost.RunDurabilityPassAsync(60.Seconds(), Token);

        var store = report.Stores.Single(x => x.Steps.Any(s => s.StepName == CatchUpAsyncProjections.StepName));
        store.Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        var step = store.Steps.Single(x => x.StepName == CatchUpAsyncProjections.StepName);
        step.Exception.ShouldBeNull();
        step.Status.ShouldBe(DurabilityPassStepStatus.Completed);

        // Before GH-5620 the step waited out its whole timeout behind its own pass's lock
        step.Elapsed.ShouldBeLessThan(CatchUpAsyncProjections.Timeout);

        await using var query = theHost.DocumentStore().QuerySession();
        var counter = await query.LoadAsync<GapCounter>(streamId, Token);
        counter.ShouldNotBeNull().Count.ShouldBe(2);
    }
}

public class CatchUpAsyncProjections(IDocumentStore store) : IDurabilityPassStep
{
    public const string StepName = "Catch up async projections";
    public static readonly TimeSpan Timeout = 15.Seconds();

    public string Name => StepName;

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await daemon.WaitForNonStaleData(Timeout);
        }
        finally
        {
            await daemon.StopAllAsync();
        }

        return DurabilityPassStepResult.Nothing;
    }
}

public record GapCounted;

public class GapCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(GapCounted _) => Count++;
}
