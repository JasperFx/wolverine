using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Subscriptions;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Fisher.Distribution;
using Wolverine.Runtime.Agents;
using Wolverine.Fisher.Publishing;
using Wolverine.Fisher.Subscriptions;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.Sqlite;

namespace Wolverine.Fisher;

public class AncillaryFisherIntegration
{
    /// <summary>
    /// Optionally move the Wolverine envelope storage to a separate schema.
    /// The recommendation would be to either leave this null, or use the same
    /// schema name as the main Fisher store
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    ///     In the case of Fisher using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Fisher configuration when possible
    /// </summary>
    public string? MainConnectionString { get; set; }

    /// <summary>
    /// Optionally override whether to automatically create message database schema objects.
    /// </summary>
    public AutoCreate? AutoCreate { get; set; }
}

public static class AncillaryWolverineOptionsFisherExtensions
{
    /// <summary>
    ///     Integrate Fisher with Wolverine's persistent outbox and add Fisher-specific middleware
    ///     to Wolverine for an ancillary/secondary document store
    /// </summary>
    /// <param name="expression">The Fisher store expression from AddFisherStore&lt;T&gt;()</param>
    /// <param name="configure">Optional configuration of ancillary Fisher integration</param>
    public static FisherStoreConfigurationExpression<T> IntegrateWithWolverine<T>(
        this FisherStoreConfigurationExpression<T> expression,
        Action<AncillaryFisherIntegration>? configure = null) where T : class, IDocumentStore
    {
        var integration = new AncillaryFisherIntegration();
        configure?.Invoke(integration);

        expression.Services.AddSingleton<IConfigureFisher<T>, FisherOverrides<T>>();

        // GH-3365: do NOT bridge JasperFx.Events.IEventStore for T here, unlike the Polecat twin.
        // Polecat's AddPolecatStore<T>() does not register one, which is why that integration must.
        // Fisher's AddFisherStore<T>() DOES:
        //
        //     services.AddSingleton<JasperFx.Events.IEventStore>(sp
        //         => (JasperFx.Events.IEventStore)UnwrapForTooling(sp.GetRequiredService<T>()));
        //
        // so a bridge here would stack with it and GetServices<IEventStore>() would hand back the
        // same store twice - double-counting the ancillary store in everything that enumerates the
        // registered stores. fisher#70 records the correction to fisher#68, which claimed otherwise.

        expression.Services.AddSingleton<AncillaryMessageStore>(s =>
        {
            var store = s.GetRequiredService<T>();
            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<SqliteMessageStore>>();

            // "main" for the same reason as the primary store: SQLite has no user-defined schemas.
            var schemaName = integration.SchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             "main";

            // No database-per-tenant branch, as on the primary store: a Fisher store is a SQLite
            // file, so its tenancy is a file per tenant and Wolverine's durability tables cannot
            // follow a tenant across files without a second writer per file. GH-3907.
            return BuildSingleSqliteMessageStore<T>(schemaName, integration.AutoCreate, store, runtime, logger);
        });

        // Always Fisher's own coordinator, never a Wolverine-managed distributed one: distributing
        // subscriptions across nodes presupposes several nodes sharing one event store, and an
        // ancillary Fisher store is a file like any other. GH-3907.
        expression.Services.AddSingleton<IProjectionCoordinator<T>>(s =>
            new ProjectionCoordinator<T>(s.GetRequiredService<T>(),
                s.GetRequiredService<ILogger<IProjectionCoordinator>>()));

        expression.Services.AddSingleton<OutboxedSessionFactory<T>>();

        return expression;
    }

    internal static AncillaryMessageStore BuildSingleSqliteMessageStore<T>(
        string schemaName,
        AutoCreate? autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime,
        ILogger<SqliteMessageStore> logger) where T : IDocumentStore
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Ancillary,
            ScheduledJobLockId = $"{schemaName ?? "wolverine"}:scheduled-jobs".GetDeterministicHashCode(),
            ConnectionString = store.Options.ConnectionString
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();

        // Wolverine.Sqlite's store takes a DbDataSource rather than a connection string - one pooled
        // source per file, which is what keeps this ancillary store's durability traffic to a single
        // writer against the file the ancillary Fisher store owns.
        var dataSource = new WolverineSqliteDataSource(store.Options.ConnectionString);

        return new(typeof(T),
            new SqliteMessageStore(settings, runtime.Options.Durability, dataSource, logger, sagaTypes));
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Fisher events at a time with
    /// a user defined action for an ancillary store
    /// </summary>
    public static FisherStoreConfigurationExpression<T> SubscribeToEvents<T>(
        this FisherStoreConfigurationExpression<T> expression,
        IWolverineSubscription subscription) where T : class, IDocumentStore
    {
        expression.Services.SubscribeToEvents<T>(subscription);
        return expression;
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Fisher events at a time with
    /// a user defined action for an ancillary store
    /// </summary>
    public static IServiceCollection SubscribeToEvents<T>(this IServiceCollection services,
        IWolverineSubscription subscription) where T : IDocumentStore
    {
        services.ConfigureFisher<T>((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();
            opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
        });

        return services;
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Fisher events at a time with
    /// a user defined action, resolved from the DI container
    /// </summary>
    public static FisherStoreConfigurationExpression<T> SubscribeToEventsWithServices<T, TSubscription>(
        this FisherStoreConfigurationExpression<T> expression, ServiceLifetime lifetime)
        where TSubscription : class, IWolverineSubscription
        where T : class, IDocumentStore
    {
        expression.Services.SubscribeToEventsWithServices<T, TSubscription>(lifetime);
        return expression;
    }

    /// <summary>
    /// Add a subscription built by the IoC container to a separate Fisher IDocumentStore
    /// </summary>
    public static IServiceCollection SubscribeToEventsWithServices<TStore, TSubscription>(
        this IServiceCollection services, ServiceLifetime lifetime)
        where TSubscription : class, IWolverineSubscription
        where TStore : IDocumentStore
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddSingleton<TSubscription>();
                services.ConfigureFisher<TStore>((sp, opts) =>
                {
                    var subscription = sp.GetRequiredService<TSubscription>();
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
                });
                break;

            default:
                services.AddScoped<TSubscription>();
                services.ConfigureFisher<TStore>((sp, opts) =>
                {
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new ScopedWolverineSubscriptionRunner<TSubscription>(sp, runtime));
                });
                break;
        }

        return services;
    }

    /// <summary>
    /// Create a subscription for Fisher events to be processed in strict order by Wolverine
    /// for an ancillary store
    /// </summary>
    public static FisherStoreConfigurationExpression<T> ProcessEventsWithWolverineHandlersInStrictOrder<T>(
        this FisherStoreConfigurationExpression<T> expression,
        string subscriptionName, Action<ISubscriptionOptions>? configure = null)
        where T : class, IDocumentStore
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder<T>(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    /// Create a subscription for Fisher events to be processed in strict order by Wolverine
    /// for an ancillary store
    /// </summary>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder<T>(
        this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure)
        where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigureFisher<T>((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();

            var invoker = new InlineInvoker(subscriptionName, runtime);
            var subscription = new WolverineSubscriptionRunner(invoker, runtime);

            configure?.Invoke(subscription);

            opts.Projections.Subscribe(subscription);
        });

        return services;
    }

    /// <summary>
    /// Relay events captured by Fisher to Wolverine message publishing for an ancillary store
    /// </summary>
    public static FisherStoreConfigurationExpression<T> PublishEventsToWolverine<T>(
        this FisherStoreConfigurationExpression<T> expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null)
        where T : class, IDocumentStore
    {
        expression.Services.PublishEventsToWolverine<T>(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    /// Relay events captured by Fisher to Wolverine message publishing for an ancillary store
    /// </summary>
    public static IServiceCollection PublishEventsToWolverine<T>(this IServiceCollection services,
        string subscriptionName, Action<IPublishingRelay>? configure)
        where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigureFisher<T>((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();

            var relay = new PublishingRelay(subscriptionName);
            configure?.Invoke(relay);

            var subscription = new WolverineSubscriptionRunner(relay, runtime);

            opts.Projections.Subscribe(subscription);
        });

        return services;
    }
}

internal class FisherOverrides<T> : IConfigureFisher<T> where T : IDocumentStore
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        // Fisher's DocumentMapping automatically detects IRevisioned types
        // and enables numeric revisions.

        // GH-3109: replace this ancillary store's default NulloMessageOutbox with the Wolverine
        // bridge so a projection author who calls slice.PublishMessage(...) from a RaiseSideEffects
        // override on THIS store has the message delivered through the Wolverine outbox after the
        // projection batch commits — parity with the primary store (FisherOverrides above) and the
        // Marten ancillary side. Without this the ancillary store silently drops projection-published
        // messages.
        options.Events.MessageOutbox = new FisherToWolverineOutbox(services);

        // No DaemonSettings.AsyncMode nudge, unlike the Marten and Polecat twins. Those flip to
        // ExternallyManaged when Wolverine takes over hosting the daemon; Wolverine.Fisher never
        // does, so the caller's own AddAsyncDaemon() choice for this ancillary store is left alone.
    }
}
