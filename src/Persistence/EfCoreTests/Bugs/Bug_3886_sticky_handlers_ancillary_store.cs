using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// Regression test for GH-3886. Reported by @fadrian23, and a direct follow-on to GH-3870.
//
// One message type is delivered to two Rabbit MQ queues. Each queue has its own sticky handler, and
// each sticky handler names a different enrolled DbContext with [Storage]. Each delivery's inbox row
// belongs in its own module's store.
//
// Root cause: MessageStoreCollection._messageTypeToAncillaryStore was keyed by message type name
// ALONE, and WolverineRuntime.HostService wrote one entry per chain with AddOrUpdate. Two sticky
// chains naming different stores therefore collided on a single key and the last chain iterated won
// globally, for every endpoint. Both chains carried the correct AncillaryStoreType the whole time --
// the map simply could not represent a per-endpoint answer.
//
// Different schemas in one database here for test convenience. See Bug_3886_separate_databases for
// what this same misrouting does when the modules really are separate databases.

public record MessageForTwoModules3886(Guid Id);

[StickyHandler(Bug_3886_sticky_handlers_ancillary_store.QueueA)]
[Storage(typeof(ModuleA3886DbContext))]
public sealed class MessageForTwoModules3886AHandler
{
    public void Handle(MessageForTwoModules3886 message, ModuleA3886DbContext dbContext)
    {
        dbContext.SomeModels.Add(new ModuleA3886Model { Id = message.Id });
    }
}

[StickyHandler(Bug_3886_sticky_handlers_ancillary_store.QueueB)]
[Storage(typeof(ModuleB3886DbContext))]
public sealed class MessageForTwoModules3886BHandler
{
    public void Handle(MessageForTwoModules3886 message, ModuleB3886DbContext dbContext)
    {
        dbContext.SomeModels.Add(new ModuleB3886Model { Id = message.Id });
    }
}

public sealed class ModuleA3886Model
{
    public Guid Id { get; set; }
}

public sealed class ModuleB3886Model
{
    public Guid Id { get; set; }
}

public sealed class ModuleA3886DbContext : DbContext
{
    public ModuleA3886DbContext(DbContextOptions<ModuleA3886DbContext> options) : base(options)
    {
    }

    public DbSet<ModuleA3886Model> SomeModels => Set<ModuleA3886Model>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("bug3886_module_a");
        modelBuilder.Entity<ModuleA3886Model>().ToTable("some_models");
    }
}

public sealed class ModuleB3886DbContext : DbContext
{
    public ModuleB3886DbContext(DbContextOptions<ModuleB3886DbContext> options) : base(options)
    {
    }

    public DbSet<ModuleB3886Model> SomeModels => Set<ModuleB3886Model>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("bug3886_module_b");
        modelBuilder.Entity<ModuleB3886Model>().ToTable("some_models");
    }
}

public class Bug_3886_sticky_handlers_ancillary_store : IAsyncLifetime
{
    public const string QueueA = "bug3886-module-a-queue";
    public const string QueueB = "bug3886-module-b-queue";
    private const string Exchange = "bug3886-exchange";

    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public Bug_3886_sticky_handlers_ancillary_store(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Other handlers in this assembly need persistence providers this host does not
                // register; only the two handlers under test matter here.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MessageForTwoModules3886AHandler>()
                    .IncludeType<MessageForTwoModules3886BHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // One logical message, two physical deliveries: the destination has to be part of the
                // inbox identity or the second delivery is rejected as a duplicate
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<MessageForTwoModules3886>()
                    .ToRabbitExchange(Exchange, e =>
                    {
                        e.BindQueue(QueueA);
                        e.BindQueue(QueueB);
                    })
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(QueueA).Named(QueueA).UseDurableInbox();
                opts.ListenToRabbitQueue(QueueB).Named(QueueB).UseDurableInbox();

                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<ModuleA3886DbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "bug3886_module_a_wolverine");
                opts.Services.AddDbContextWithWolverineIntegration<ModuleB3886DbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "bug3886_module_b_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3886_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "bug3886_module_a_wolverine", MessageStoreRole.Ancillary).Enroll<ModuleA3886DbContext>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "bug3886_module_b_wolverine", MessageStoreRole.Ancillary).Enroll<ModuleB3886DbContext>();

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
    public async Task each_sticky_handler_stores_its_envelope_in_its_own_module_store()
    {
        var runtime = _host.GetRuntime();
        var messageTypeName = typeof(MessageForTwoModules3886).ToMessageTypeName();

        // If this ever regresses, these two dumps say immediately whether the chains lost their
        // store association (an attribute-discovery bug, as in GH-2576/2944/3870) or whether the
        // chains are right and the routing lost the per-endpoint distinction (GH-3886).
        foreach (var chain in runtime.Handlers.AllChains()
                     .Where(x => x.MessageType == typeof(MessageForTwoModules3886)))
        {
            _output.WriteLine(
                $"chain endpoints=[{chain.Endpoints.Select(x => x.EndpointName).Join(", ")}] " +
                $"AncillaryStoreType={chain.AncillaryStoreType?.Name ?? "null"}");
        }

        foreach (var queue in new[] { QueueA, QueueB })
        {
            var uri = new Uri($"rabbitmq://queue/{queue}");
            _output.WriteLine(
                $"routed store for {queue} => {runtime.Stores!.TryFindAncillaryStoreForMessageType(uri, messageTypeName)?.Uri.ToString() ?? "MAIN"}");
        }

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new MessageForTwoModules3886(Guid.NewGuid()));

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        var storeA = runtime.Stores!.FindAncillaryStore(typeof(ModuleA3886DbContext));
        var storeB = runtime.Stores.FindAncillaryStore(typeof(ModuleB3886DbContext));

        var inA = (await storeA.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();
        var inB = (await storeB.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();
        var inMain = (await runtime.Storage.Admin.AllIncomingAsync())
            .Where(x => x.MessageType == messageTypeName).ToArray();

        inA.Length.ShouldBe(1, "The module-a-queue delivery belongs in the ModuleA store");
        inB.Length.ShouldBe(1, "The module-b-queue delivery belongs in the ModuleB store");
        inMain.ShouldBeEmpty("Neither delivery belongs in the main store");

        // Each store got the delivery from ITS OWN queue, not just one row apiece
        inA.Single().Destination.ShouldBe(new Uri($"rabbitmq://queue/{QueueA}"));
        inB.Single().Destination.ShouldBe(new Uri($"rabbitmq://queue/{QueueB}"));
    }
}
