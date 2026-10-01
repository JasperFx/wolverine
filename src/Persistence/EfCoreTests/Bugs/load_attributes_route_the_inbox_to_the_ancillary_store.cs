using IntegrationTests;
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

// GH-3870 inbox routing for handlers that reach their DbContext only through a load attribute.
// Reuses GH-3870's DbContext, entity and stores.

public record LoadOnHandlerMessage3870(Guid Id);

public record LoadInBeforeMessage3870(Guid Id);

public record LoadInBeforeTransactionalMessage3870(Guid Id);

public record LoadInBeforeDesignatedMessage3870(Guid Id);

// A second DbContext candidate, so the transaction owner has to be designated
public sealed class Lookup3870DbContext : DbContext
{
    public Lookup3870DbContext(DbContextOptions<Lookup3870DbContext> options) : base(options)
    {
    }
}

[WolverineIgnore]
public static class LoadOnHandlerMessage3870Handler
{
    public static void Handle(LoadOnHandlerMessage3870 message, [All] IReadOnlyList<ModelInModule3870> models)
    {
    }
}

[WolverineIgnore]
public static class LoadInBeforeMessage3870Handler
{
    public static void Before([All] IReadOnlyList<ModelInModule3870> models)
    {
    }

    public static void Handle(LoadInBeforeMessage3870 message)
    {
    }
}

[WolverineIgnore]
public static class LoadInBeforeTransactionalMessage3870Handler
{
    public static void Before([All] IReadOnlyList<ModelInModule3870> models)
    {
    }

    [Transactional]
    public static void Handle(LoadInBeforeTransactionalMessage3870 message)
    {
    }
}

[WolverineIgnore]
public static class LoadInBeforeDesignatedMessage3870Handler
{
    public static void Before([All] IReadOnlyList<ModelInModule3870> models)
    {
    }

    [Transactional(typeof(Module3870DbContext))]
    public static void Handle(LoadInBeforeDesignatedMessage3870 message, Lookup3870DbContext lookups)
    {
    }
}

public class load_attributes_route_the_inbox_to_the_ancillary_store : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = "load3870_" + Guid.NewGuid().ToString("N")[..8];

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoadOnHandlerMessage3870Handler))
                    .IncludeType(typeof(LoadInBeforeMessage3870Handler))
                    .IncludeType(typeof(LoadInBeforeTransactionalMessage3870Handler))
                    .IncludeType(typeof(LoadInBeforeDesignatedMessage3870Handler));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages().ToRabbitQueue(_queueName).UseDurableOutbox();
                opts.ListenToRabbitQueue(_queueName).UseDurableInbox();

                opts.Services.AddDbContext<Lookup3870DbContext>(x => x.UseNpgsql(Servers.PostgresConnectionString));

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

    public static IEnumerable<TheoryDataRow<Type>> Messages() =>
    [
        new(typeof(LoadOnHandlerMessage3870)),
        new(typeof(LoadInBeforeMessage3870)),
        new(typeof(LoadInBeforeTransactionalMessage3870)),
        new(typeof(LoadInBeforeDesignatedMessage3870))
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task envelope_is_handled_in_the_store_enrolled_to_the_loaded_dbcontext(Type messageType)
    {
        var message = Activator.CreateInstance(messageType, Guid.NewGuid())!;

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(message);

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        var messageTypeName = messageType.ToMessageTypeName();

        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(Module3870DbContext));
        var inAncillary = await ancillaryStore.Admin.AllIncomingAsync();

        inAncillary.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Handled)
            .ShouldNotBeEmpty(
                "The envelope should be marked Handled in the store enrolled to the DbContext the handler loads " +
                "through, so that the inbox update and the EF Core transaction are one.");

        var inMain = await runtime.Storage.Admin.AllIncomingAsync();

        inMain.Where(x => x.MessageType == messageTypeName && x.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("The envelope should not be left Incoming in the main store.");
    }
}
