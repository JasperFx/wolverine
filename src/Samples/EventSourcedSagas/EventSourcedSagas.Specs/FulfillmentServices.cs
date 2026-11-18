using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.Mistakes;
using IntegrationTests;
using JasperFx.Core;
using Wolverine.ErrorHandling;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace EventSourcedSagas.Specs;

/// <summary>
/// The "fulfillment" service: the OrderFulfillment saga (and the SupportTicket mistake) on Marten, with durable local
/// queues so a failed message is dead-lettered where a specification can read it, and completed sagas retained for 30
/// days (GH-5572).
/// </summary>
public abstract class FulfillmentServices : IAsyncLifetime
{
    public const string ServiceName = "fulfillment";

    public static readonly TimeSpan Retention = 30.Days();

    public IHost Host { get; private set; } = null!;

    /// <summary>A schema of the fixture's own, so the fixtures' streams and dead letters never meet.</summary>
    protected abstract string Schema { get; }

    /// <summary>Anything only this fixture's event store has. Nothing, unless a fixture says otherwise.</summary>
    protected virtual void ConfigureMarten(StoreOptions options)
    {
    }

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = ServiceName;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = Schema;
                        m.DisableNpgsqlLogging = true;
                        ConfigureMarten(m);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.RetainCompletedSagas(Retention);
                opts.Services.AddSingleton<PaymentLedger>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(OrderFulfillment))
                    .IncludeType(typeof(SupportTicket))
                    .IncludeType(typeof(Payments));

                // Retried twice, then dead-lettered (or compensated)
                opts.OnException<Exception>().RetryTimes(2);
            })
            .StartAsync();

        await ResetAsync();
    }

    /// <summary>Every specification starts from empty stores and a working ledger.</summary>
    public async Task ResetAsync()
    {
        await Host.ResetAllMartenDataAsync();
        await Host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();
        Host.Services.GetRequiredService<PaymentLedger>().Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>The default: Wolverine registers the OrderFulfillment saga as an Inline snapshot itself.</summary>
public class InlineFulfillmentServices : FulfillmentServices
{
    protected override string Schema => "es_sagas";
}

/// <summary>The application registers the OrderFulfillment saga as a Live aggregation, which wins over Wolverine's default.</summary>
public class LiveFulfillmentServices : FulfillmentServices
{
    protected override string Schema => "es_sagas_live";

    protected override void ConfigureMarten(StoreOptions options)
        => options.Projections.LiveStreamAggregation<OrderFulfillment>();
}

[CollectionDefinition(nameof(FulfillmentCollection))]
public class FulfillmentCollection : ICollectionFixture<InlineFulfillmentServices>;

[CollectionDefinition(nameof(LiveFulfillmentCollection))]
public class LiveFulfillmentCollection : ICollectionFixture<LiveFulfillmentServices>;
