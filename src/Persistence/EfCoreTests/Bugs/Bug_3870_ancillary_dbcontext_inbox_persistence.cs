using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// Regression test for GH-3870. Reported by @fadrian23.
//
// A handler takes an EF Core DbContext that has been enrolled in an ancillary message store with
// .Enroll<TDbContext>(). Before the fix its durable inbox envelope was written to the MAIN store
// instead of the ancillary one.
//
// Root cause: WolverineRuntime.HostService builds the message-type-to-ancillary-store map from
// chain.AncillaryStoreType, and that property is only ever populated from the [Storage] /
// [MartenStore] / [PolecatStore] attribute family (StorageAttributeEagerPolicy). A handler that
// merely *injects* an enrolled DbContext names no attribute, so no map entry existed, DurableReceiver
// left envelope.Store null, and the row went to the main store. The handler then committed through
// EfCoreEnvelopeTransaction against the DbContext's own Wolverine schema, where marking the envelope
// handled updated zero rows -- leaving nothing Handled in the ancillary store and an orphan Incoming
// row in the main store.
//
// This matters most when the two stores are separate physical databases: with the inbox row in the
// main store there is no way to mark the message processed in the same transaction as the handler's
// EF Core writes, so a failure window exists where business data commits but the message can be
// redelivered. Different schemas in one database here for test convenience only.

public record MessageForEnrolledDbContext3870(Guid Id);

public sealed class ModelInModule3870
{
    public Guid Id { get; set; }
}

public sealed class Module3870DbContext : DbContext
{
    public Module3870DbContext(DbContextOptions<Module3870DbContext> options) : base(options)
    {
    }

    public DbSet<ModelInModule3870> SomeModels => Set<ModelInModule3870>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("bug3870_module");
        modelBuilder.Entity<ModelInModule3870>().ToTable("some_models");
    }
}

// Deliberately no [Storage] attribute -- taking the enrolled DbContext as a dependency is the only
// thing marking this handler as belonging to the ancillary store, and that is the whole point.
public sealed class MessageForEnrolledDbContext3870Handler
{
    public void Handle(MessageForEnrolledDbContext3870 message, Module3870DbContext dbContext)
    {
        dbContext.SomeModels.Add(new ModelInModule3870 { Id = message.Id });
    }
}

public class Bug_3870_ancillary_dbcontext_inbox_persistence : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = "bug3870_" + Guid.NewGuid().ToString("N")[..8];

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Other handlers in this assembly need persistence providers this host does not
                // register; only the one handler under test matters here.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MessageForEnrolledDbContext3870Handler>();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<MessageForEnrolledDbContext3870>()
                    .ToRabbitQueue(_queueName)
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(_queueName).UseDurableInbox();

                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<Module3870DbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString),
                    "bug3870_module_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3870_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                        "bug3870_module_wolverine", MessageStoreRole.Ancillary)
                    .Enroll<Module3870DbContext>();

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task envelope_is_stored_and_handled_in_the_enrolled_dbcontext_store()
    {
        var message = new MessageForEnrolledDbContext3870(Guid.NewGuid());

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(message);

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        var messageTypeName = typeof(MessageForEnrolledDbContext3870).ToMessageTypeName();

        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(Module3870DbContext));
        var inAncillary = await ancillaryStore.Admin.AllIncomingAsync();

        inAncillary.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Handled)
            .ShouldNotBeEmpty(
                "The envelope should be marked Handled in the store enrolled to the handler's DbContext, " +
                "so that the inbox update and the EF Core writes share one transaction.");

        var inMain = await runtime.Storage.Admin.AllIncomingAsync();

        inMain.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("The envelope should not be left Incoming in the main store.");
    }
}
