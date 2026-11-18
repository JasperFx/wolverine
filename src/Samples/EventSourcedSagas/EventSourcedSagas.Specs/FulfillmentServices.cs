using Bobcat.CritterStack;
using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.GiftCards;
using EventSourcedSagas.Returns;
using EventSourcedSagas.Support;
using Fisher;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Polecat;
using Wolverine.Runtime;

namespace EventSourcedSagas.Specs;

/// <summary>
/// The "fulfillment" service: the OrderFulfillment, SupportTicket and ReturnRequest sagas on one event store, with durable
/// local queues so a failed message is dead-lettered where a specification can read it, and completed sagas retained for
/// 30 days (GH-5572). One subclass per store: the same specifications run on Marten, Polecat and Fisher.
/// </summary>
public abstract class FulfillmentServices : IAsyncLifetime
{
    public const string ServiceName = "fulfillment";

    public static readonly TimeSpan Retention = 30.Days();

    public IHost Host { get; private set; } = null!;

    /// <summary>The event store, integrated with Wolverine, and any saga types only this store's fixture has</summary>
    protected abstract void configureStore(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = ServiceName;

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.RetainCompletedSagas(Retention);
                opts.Services.AddSingleton<PaymentLedger>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(OrderFulfillment))
                    .IncludeType(typeof(SupportTicket))
                    .IncludeType(typeof(ReturnRequest))
                    .IncludeType(typeof(Payments));

                // Retried twice, then dead-lettered (or compensated)
                opts.OnException<Exception>().RetryTimes(2);

                configureStore(opts);
            })
            .StartAsync();

        await ResetAsync();
    }

    /// <summary>Every specification starts from empty stores and a working ledger.</summary>
    public async Task ResetAsync()
    {
        await Host.ResetEventStoresAsync();
        await Host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();
        Host.Services.GetRequiredService<PaymentLedger>().Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>Marten. Wolverine registers each event-sourced saga as an Inline snapshot itself.</summary>
public class InlineFulfillmentServices : FulfillmentServices
{
    protected virtual string Schema => "es_sagas";

    protected virtual void configureMarten(Marten.StoreOptions options)
    {
    }

    protected override void configureStore(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = Schema;
                m.DisableNpgsqlLogging = true;
                configureMarten(m);
            })
            .IntegrateWithWolverine();

        // An ancillary store, where the GiftCard saga lives
        opts.Services.AddMartenStore<IGiftCardStore>(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = $"{Schema}_giftcards";
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();

        opts.Discovery.IncludeType(typeof(GiftCard));
    }
}

/// <summary>Marten, where the application registers the OrderFulfillment saga as a Live aggregation, which wins.</summary>
public class LiveFulfillmentServices : InlineFulfillmentServices
{
    protected override string Schema => "es_sagas_live";

    protected override void configureMarten(Marten.StoreOptions options)
        => options.Projections.LiveStreamAggregation<OrderFulfillment>();
}

/// <summary>Polecat, on SQL Server.</summary>
public class PolecatFulfillmentServices : FulfillmentServices
{
    protected override void configureStore(WolverineOptions opts)
    {
        opts.Services.AddPolecat(p =>
            {
                p.ConnectionString = Servers.SqlServerConnectionString;
                p.DatabaseSchemaName = "es_sagas";
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        // An ancillary store, where the PolecatGiftCard saga lives
        opts.Services.AddPolecatStore<IPolecatGiftCardStore>(p =>
            {
                p.Connection(Servers.SqlServerConnectionString);
                p.DatabaseSchemaName = "es_sagas_giftcards";
            })
            .IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();

        opts.Discovery.IncludeType(typeof(PolecatGiftCard));
    }
}

/// <summary>Fisher, on a SQLite file of its own.</summary>
public class FisherFulfillmentServices : FulfillmentServices
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"es_sagas_{Guid.NewGuid():N}.db");

    protected override void configureStore(WolverineOptions opts)
    {
        opts.Services.AddFisher(f =>
            {
                f.Connection($"Data Source={_file}");
                f.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
    }
}

[CollectionDefinition(nameof(FulfillmentCollection))]
public class FulfillmentCollection : ICollectionFixture<InlineFulfillmentServices>;

[CollectionDefinition(nameof(LiveFulfillmentCollection))]
public class LiveFulfillmentCollection : ICollectionFixture<LiveFulfillmentServices>;

[CollectionDefinition(nameof(PolecatFulfillmentCollection))]
public class PolecatFulfillmentCollection : ICollectionFixture<PolecatFulfillmentServices>;

[CollectionDefinition(nameof(FisherFulfillmentCollection))]
public class FisherFulfillmentCollection : ICollectionFixture<FisherFulfillmentServices>;
