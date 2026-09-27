using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.DomainEvents;

/// <summary>
/// GH-4628. GH-3744 found that on a Wolverine-mapped DbContext a durable route persists its envelope by
/// *adding* an OutgoingMessage/IncomingMessage entity to the change tracker, that domain event scraping
/// runs AFTER the SaveChangesAsync the EF Core transactional middleware emits, and that committing
/// straight after the scrape therefore commits the aggregate and drops every envelope the scrape just
/// produced. The event is still published in memory, so tracked-session assertions stay green -- the
/// only symptom is the missing durability row, which is exactly why the sibling tests in
/// configuration_of_domain_events_scrapers.cs never caught it.
///
/// That fix landed only in the tenanted frame (CommitTenantedDbContextTransaction). These tests cover
/// the single, non-tenanted Eager path through EfCoreEnvelopeTransaction.CommitAsync, and they assert
/// the database row rather than the tracked session.
/// </summary>
[Collection("sqlserver")]
public class scraped_domain_events_persist_envelopes_4628 : IAsyncDisposable
{
    private const string SchemaName = "de4628";

    private IHost theHost = null!;

    public async ValueTask DisposeAsync()
    {
        // Never leave a gated handler parked on a host that is going away
        GatedDomainEventHandler.Release.TrySetResult();

        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task startHostAsync(Action<WolverineOptions> configure)
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.PublishDomainEventsFromEntityFrameworkCore();

                configure(opts);

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    [Fact]
    public async Task scraped_domain_events_on_a_durable_route_persist_a_row_before_commit()
    {
        GatedDomainEventHandler.Reset();

        await startHostAsync(opts => opts.Policies.UseDurableLocalQueues());

        try
        {
            // Not a tracked session on purpose -- the whole point is that the in-memory publish
            // succeeds while the durability row is missing
            await theHost.MessageBus().InvokeAsync(new RaiseGatedDomainEvent(Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

            // The handler for the scraped domain event parks on a gate, so the inbox row -- if it was
            // ever written -- is still there to be seen
            await GatedDomainEventHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            var count = await countIncomingEnvelopesAsync(GatedDomainEvent.MessageTypeName);

            count.ShouldBe(1);
        }
        finally
        {
            GatedDomainEventHandler.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task handled_row_for_a_non_inbox_envelope_is_persisted_before_commit()
    {
        // No durable local queues here, so the incoming envelope was never persisted in the inbox and
        // EfCoreEnvelopeTransaction.CommitAsync takes its PersistIncomingAsync(handled row) branch --
        // which has the same "only Add()s to the change tracker" shape as the scrape above
        await startHostAsync(_ => { });

        await theHost.SendMessageAndWaitAsync(new BufferedRoutedCommand(Guid.CreateVersion7()));

        var count = await countIncomingEnvelopesAsync(BufferedRoutedCommand.MessageTypeName);

        count.ShouldBe(1);
    }

    private static async Task<int> countIncomingEnvelopesAsync(string messageType)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"select count(*) from {SchemaName}.wolverine_incoming_envelopes where message_type = @type";
        command.Parameters.AddWithValue("@type", messageType);

        var raw = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(raw);
    }
}

public record RaiseGatedDomainEvent(Guid Id);

[MessageIdentity(GatedDomainEvent.MessageTypeName)]
public record GatedDomainEvent(Guid Id) : IDomainEvent
{
    public const string MessageTypeName = "gh4628-gated-domain-event";
}

[MessageIdentity(BufferedRoutedCommand.MessageTypeName)]
public record BufferedRoutedCommand(Guid Id)
{
    public const string MessageTypeName = "gh4628-buffered-routed-command";
}

public static class GatedDomainEventHandler
{
    public static TaskCompletionSource Started { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource Release { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Taking the DbContext is what applies the EF Core transactional middleware
    public static void Handle(RaiseGatedDomainEvent command, OutgoingDomainEvents events, CleanDbContext dbContext)
    {
        events.Add(new GatedDomainEvent(command.Id));
    }

    public static async Task Handle(GatedDomainEvent e)
    {
        Started.TrySetResult();

        // Bounded so a failing test can never wedge the host on shutdown
        try
        {
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            // fall through, let the message complete
        }
    }

    public static void Handle(BufferedRoutedCommand command, CleanDbContext dbContext)
    {
    }
}
