// NOTE: This file requires Polecat 1.1+ (AddPolecatStore<T>, IConfigurePolecat<T>, PolecatStoreExpression<T>)
// Uncomment #if POLECAT_1_1 / #endif when ready, or remove the guards after upgrading the Polecat NuGet
#if POLECAT_1_1
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Subscriptions;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Polecat.Distribution;
using Wolverine.Polecat.Publishing;
using Wolverine.Polecat.Subscriptions;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.Polecat;

public class AncillaryPolecatIntegration
{
    /// <summary>
    /// Optionally move the Wolverine envelope storage to a separate schema.
    /// The recommendation would be to either leave this null, or use the same
    /// schema name as the main Polecat store
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    ///     In the case of Polecat using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Polecat configuration when possible
    /// </summary>
    public string? MainConnectionString { get; set; }

    /// <summary>
    /// Optionally override whether to automatically create message database schema objects.
    /// </summary>
    public AutoCreate? AutoCreate { get; set; }
}

public static class AncillaryWolverineOptionsPolecatExtensions
{
    /// <summary>
    ///     Integrate Polecat with Wolverine's persistent outbox and add Polecat-specific middleware
    ///     to Wolverine for an ancillary/secondary document store
    /// </summary>
    /// <param name="expression">The Polecat store expression from AddPolecatStore&lt;T&gt;()</param>
    /// <param name="configure">Optional configuration of ancillary Polecat integration</param>
    public static PolecatStoreExpression<T> IntegrateWithWolverine<T>(
        this PolecatStoreExpression<T> expression,
        Action<AncillaryPolecatIntegration>? configure = null) where T : class, IDocumentStore
    {
        var integration = new AncillaryPolecatIntegration();
        configure?.Invoke(integration);

        expression.Services.AddSingleton<IConfigurePolecat<T>, PolecatOverrides<T>>();

        expression.Services.AddSingleton<AncillaryMessageStore>(s =>
        {
            var store = s.GetRequiredService<T>();
            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<SqlServerMessageStore>>();

            var schemaName = integration.SchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             "wolverine";

            if (store.Options.Tenancy != null &&
                store.Options.Tenancy.GetType().Name != "DefaultTenancy")
            {
                return BuildMultiTenantedMessageDatabase<T>(schemaName, integration.AutoCreate,
                    integration.MainConnectionString, store, runtime);
            }

            return BuildSingleSqlServerMessageStore<T>(schemaName, integration.AutoCreate, store, runtime, logger);
        });

        expression.Services.AddSingleton<EventSubscriptionAgentFamily>();

        expression.Services.AddSingleton<IProjectionCoordinator<T>>(s =>
        {
            var polecatIntegration = s.GetService<PolecatIntegration>();
            var store = s.GetRequiredService<T>();

            if (polecatIntegration == null || !polecatIntegration.UseWolverineManagedEventSubscriptionDistribution)
            {
                return new ProjectionCoordinator<T>(store,
                    s.GetRequiredService<ILogger<IProjectionCoordinator>>());
            }

            var agents = s.GetRequiredService<EventSubscriptionAgentFamily>();
            return new WolverineProjectionCoordinator<T>(agents, store);
        });

        expression.Services.AddSingleton<OutboxedSessionFactory<T>>();

        return expression;
    }

    internal static AncillaryMessageStore BuildMultiTenantedMessageDatabase<T>(
        string schemaName,
        AutoCreate? autoCreate,
        string? masterDatabaseConnectionString,
        IDocumentStore store,
        IWolverineRuntime runtime) where T : IDocumentStore
    {
        var connectionString = masterDatabaseConnectionString ?? store.Options.ConnectionString;

        if (connectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                "There is no configured connectivity for the required master SQL Server message database");
        }

        var mainSettings = new DatabaseSettings
        {
            ConnectionString = connectionString,
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Ancillary,
            CommandQueuesEnabled = true
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();
        var master = new SqlServerMessageStore(mainSettings, runtime.Options.Durability,
            runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>(), sagaTypes)
        {
            Name = "Master",
        };

        var source = new PolecatMessageDatabaseSource<T>(schemaName,
            autoCreate ?? AutoCreate.CreateOrUpdate, store.As<T>(), runtime);

        master.Initialize(runtime);

        return new(typeof(T),
            new Wolverine.Persistence.Durability.MultiTenantedMessageStore(master, runtime, source));
    }

    internal static AncillaryMessageStore BuildSingleSqlServerMessageStore<T>(
        string schemaName,
        AutoCreate? autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime,
        ILogger<SqlServerMessageStore> logger) where T : IDocumentStore
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
        return new(typeof(T),
            new SqlServerMessageStore(settings, runtime.Options.Durability, logger, sagaTypes));
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Polecat events at a time with
    /// a user defined action for an ancillary store
    /// </summary>
    public static PolecatStoreExpression<T> SubscribeToEvents<T>(
        this PolecatStoreExpression<T> expression,
        IWolverineSubscription subscription) where T : class, IDocumentStore
    {
        expression.Services.SubscribeToEvents<T>(subscription);
        return expression;
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Polecat events at a time with
    /// a user defined action for an ancillary store
    /// </summary>
    public static IServiceCollection SubscribeToEvents<T>(this IServiceCollection services,
        IWolverineSubscription subscription) where T : IDocumentStore
    {
        services.ConfigurePolecat<T>((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();
            opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
        });

        return services;
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Polecat events at a time with
    /// a user defined action, resolved from the DI container
    /// </summary>
    public static PolecatStoreExpression<T> SubscribeToEventsWithServices<T, TSubscription>(
        this PolecatStoreExpression<T> expression, ServiceLifetime lifetime)
        where TSubscription : class, IWolverineSubscription
        where T : class, IDocumentStore
    {
        expression.Services.SubscribeToEventsWithServices<T, TSubscription>(lifetime);
        return expression;
    }

    /// <summary>
    /// Add a subscription built by the IoC container to a separate Polecat IDocumentStore
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
                services.ConfigurePolecat<TStore>((sp, opts) =>
                {
                    var subscription = sp.GetRequiredService<TSubscription>();
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
                });
                break;

            default:
                services.AddScoped<TSubscription>();
                services.ConfigurePolecat<TStore>((sp, opts) =>
                {
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new ScopedWolverineSubscriptionRunner<TSubscription>(sp, runtime));
                });
                break;
        }

        return services;
    }

    /// <summary>
    /// Create a subscription for Polecat events to be processed in strict order by Wolverine
    /// for an ancillary store
    /// </summary>
    public static PolecatStoreExpression<T> ProcessEventsWithWolverineHandlersInStrictOrder<T>(
        this PolecatStoreExpression<T> expression,
        string subscriptionName, Action<ISubscriptionOptions>? configure = null)
        where T : class, IDocumentStore
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder<T>(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    /// Create a subscription for Polecat events to be processed in strict order by Wolverine
    /// for an ancillary store
    /// </summary>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder<T>(
        this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure)
        where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigurePolecat<T>((sp, opts) =>
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
    /// Relay events captured by Polecat to Wolverine message publishing for an ancillary store
    /// </summary>
    public static PolecatStoreExpression<T> PublishEventsToWolverine<T>(
        this PolecatStoreExpression<T> expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null)
        where T : class, IDocumentStore
    {
        expression.Services.PublishEventsToWolverine<T>(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    /// Relay events captured by Polecat to Wolverine message publishing for an ancillary store
    /// </summary>
    public static IServiceCollection PublishEventsToWolverine<T>(this IServiceCollection services,
        string subscriptionName, Action<IPublishingRelay>? configure)
        where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigurePolecat<T>((sp, opts) =>
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

internal class PolecatOverrides<T> : IConfigurePolecat<T> where T : IDocumentStore
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        // Polecat's DocumentMapping automatically detects IRevisioned types
        // and enables numeric revisions
    }
}
#endif
