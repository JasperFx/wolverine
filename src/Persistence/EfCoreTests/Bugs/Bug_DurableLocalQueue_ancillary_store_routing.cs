using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace EfCoreTests.Bugs;

#region Test Infrastructure

/// <summary>
/// A message whose handler uses the ancillary DbContext.
/// </summary>
public record AncillaryLocalQueueMessage(Guid Id, string Name);

/// <summary>
/// A message whose handler uses the main DbContext.
/// </summary>
public record MainLocalQueueMessage(Guid Id, string Name);

/// <summary>
/// DbContext for the ancillary store (simulating a separate module database).
/// </summary>
public class AncillaryLocalQueueDbContext(DbContextOptions<AncillaryLocalQueueDbContext> options) : DbContext(options)
{
    public DbSet<AncillaryLocalQueueDoc> Docs => Set<AncillaryLocalQueueDoc>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dlq_ancillary");
        modelBuilder.MapWolverineEnvelopeStorage("dlq_ancillary");

        modelBuilder.Entity<AncillaryLocalQueueDoc>(map =>
        {
            map.ToTable("docs");
            map.HasKey(x => x.Id);
        });
    }
}

/// <summary>
/// DbContext for the main store.
/// </summary>
public class MainLocalQueueDbContext(DbContextOptions<MainLocalQueueDbContext> options) : DbContext(options)
{
    public DbSet<MainLocalQueueDoc> Docs => Set<MainLocalQueueDoc>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dlq_main");
        modelBuilder.MapWolverineEnvelopeStorage("dlq_main");

        modelBuilder.Entity<MainLocalQueueDoc>(map =>
        {
            map.ToTable("docs");
            map.HasKey(x => x.Id);
        });
    }
}

public class AncillaryLocalQueueDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class MainLocalQueueDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

/// <summary>
/// Handler that uses the ancillary DbContext. Wolverine's code generation
/// will detect the AncillaryLocalQueueDbContext dependency and, because it
/// was enrolled as an ancillary store, will insert ApplyAncillaryStoreFrame
/// into the handler chain.
/// </summary>
public static class AncillaryLocalQueueMessageHandler
{
    public static void Handle(AncillaryLocalQueueMessage message, AncillaryLocalQueueDbContext db)
    {
        db.Docs.Add(new AncillaryLocalQueueDoc { Id = message.Id, Name = message.Name });
    }
}

/// <summary>
/// Handler that uses the main DbContext.
/// </summary>
public static class MainLocalQueueMessageHandler
{
    public static void Handle(MainLocalQueueMessage message, MainLocalQueueDbContext db)
    {
        db.Docs.Add(new MainLocalQueueDoc { Id = message.Id, Name = message.Name });
    }
}

#endregion

/// <summary>
/// Verifies that DurableLocalQueue correctly routes incoming envelope
/// persistence to ancillary stores when the handler targets a DbContext
/// enrolled with an ancillary message store.
///
/// Before the fix, DurableLocalQueue used runtime.Storage.Inbox directly
/// (always the main store), ignoring the envelope.Store property that was
/// set by ApplyAncillaryStoreFrame. This caused:
/// 1. The incoming envelope to be persisted in the main store
/// 2. The mark-as-handled to target the ancillary store (via DelegatingMessageInbox
///    in DurableReceiver)
/// 3. The envelope to be stuck as Incoming in the main store forever
/// </summary>
[Collection("postgresql")]
public class Bug_DurableLocalQueue_ancillary_store_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // Clean up schemas from previous runs
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DROP SCHEMA IF EXISTS dlq_main CASCADE; DROP SCHEMA IF EXISTS dlq_ancillary CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }
        await conn.CloseAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Main EF Core DbContext + message store
                opts.Services.AddDbContextWithWolverineIntegration<MainLocalQueueDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dlq_main");

                // Ancillary EF Core DbContext + message store (same server, different schema)
                opts.Services.AddDbContextWithWolverineIntegration<AncillaryLocalQueueDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(
                        Servers.PostgresConnectionString, "dlq_ancillary",
                        MessageStoreRole.Ancillary)
                    .Enroll<AncillaryLocalQueueDbContext>();

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                // Force durable local queues — this is what triggers DurableLocalQueue
                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryLocalQueueMessageHandler))
                    .IncludeType(typeof(MainLocalQueueMessageHandler));

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task ancillary_handler_should_not_leave_envelope_stuck_in_main_store()
    {
        var message = new AncillaryLocalQueueMessage(Guid.NewGuid(), "test-ancillary");

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        // Give a moment for post-processing
        await Task.Delay(500);

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // The main store should NOT have any lingering Incoming envelopes
        // for the ancillary message type. Before the fix, the envelope would
        // be persisted in the main store but marked as handled in the ancillary
        // store, leaving it stuck as Incoming in the main store.
        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming
            .Where(e => e.MessageType == typeof(AncillaryLocalQueueMessage).ToMessageTypeName()
                        && e.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty(
                "Ancillary message envelope should not be stuck as Incoming in main store. " +
                "This indicates DurableLocalQueue is persisting to the main store instead of the ancillary store.");
    }

    [Fact]
    public async Task main_handler_should_work_normally()
    {
        var message = new MainLocalQueueMessage(Guid.NewGuid(), "test-main");

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        await Task.Delay(500);

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Main store messages should be handled normally
        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming
            .Where(e => e.MessageType == typeof(MainLocalQueueMessage).ToMessageTypeName()
                        && e.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("Main store message should not be stuck as Incoming");
    }

    [Fact]
    public async Task ancillary_handler_entity_changes_should_be_committed()
    {
        var message = new AncillaryLocalQueueMessage(Guid.NewGuid(), "test-entity");

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        // Verify the entity was actually saved in the ancillary DbContext
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AncillaryLocalQueueDbContext>();
        var doc = await db.Docs.FindAsync(message.Id);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("test-entity");
    }
}
