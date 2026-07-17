using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wolverine.Polecat.Distribution;
using Wolverine.Polecat.Publishing;
using Wolverine.Polecat.Subscriptions;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.SqlServer.Persistence;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Polecat;

internal class MapEventTypeMessages : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.MapGenericMessageType(typeof(IEvent<>), typeof(Event<>));
    }
}

public static class WolverineOptionsPolecatExtensions
{
    /// <summary>
    ///     Integrate Polecat with Wolverine's persistent outbox and add Polecat-specific middleware
    ///     to Wolverine
    /// </summary>
    public static PolecatConfigurationExpression IntegrateWithWolverine(
        this PolecatConfigurationExpression expression,
        Action<PolecatIntegration>? configure = null)
    {
        var integration = expression.Services.FindPolecatIntegration();
        if (integration == null)
        {
            integration = new PolecatIntegration();

            configure?.Invoke(integration);

            expression.Services.AddSingleton(integration);
            expression.Services.AddSingleton<IWolverineExtension>(integration);
        }
        else
        {
            configure?.Invoke(integration);
        }

        expression.Services.AddSingleton<IWolverineExtension, MapEventTypeMessages>();

        expression.Services.AddScoped<IPolecatOutbox, PolecatOutbox>();

        expression.Services.AddSingleton<DatabaseSettings>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>() as IMessageDatabase;
            if (store != null) return store.Settings;

            return new DatabaseSettings();
        });

        expression.Services.AddSingleton<IMessageStore>(s =>
        {
            var store = s.GetRequiredService<IDocumentStore>();
            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<SqlServerMessageStore>>();

            // Mirror Wolverine.Marten: when no message-storage schema is configured, inherit the
            // Polecat store's DatabaseSchemaName so distinct-schema Polecat services are isolated by
            // default (separate durability tables: dead letters, nodes/assignments, …) instead of
            // all sharing the "wolverine" schema. See GH-3175.
            var schemaName = integration.MessageStorageSchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             store.Options.DatabaseSchemaName ??
                             "wolverine";

            // GH-3445: honor Polecat's database-per-tenant on the primary store just like the ancillary
            // path (and the Marten twin at WolverineOptionsMartenExtensions.cs) — build a
            // MultiTenantedMessageStore over a PolecatMessageDatabaseSource so each tenant's envelope
            // storage lands in that tenant's database, with a "main" store for tenant-neutral operations.
            // Dispatch on cardinality rather than the tenancy's type name (see the ancillary seam's note
            // on marten#4864). This is where MainDatabaseConnectionString is finally read.
            if (store.Options.Tenancy != null &&
                store.Options.Tenancy.Cardinality != JasperFx.Descriptors.DatabaseCardinality.Single)
            {
                return BuildMultiTenantedMessageStore(schemaName, store, runtime,
                    integration.MainDatabaseConnectionString);
            }

            return BuildSqlServerMessageStore(schemaName, store, runtime, logger);
        });

        expression.Services.AddSingleton<IConfigurePolecat, PolecatOverrides>();

        expression.Services.AddSingleton<OutboxedSessionFactory>();

        // GH-3109: lets the provider-agnostic [Storage(typeof(IMyStore))] attribute route a handler to
        // a Polecat ancillary store by resolving this provider from the store marker type. Registered
        // here (not in PolecatIntegration.Configure) so the singleton is present in the codegen-time
        // container that StorageAttribute.Modify queries. TryAddEnumerable keeps it to one instance
        // even when multiple Polecat stores integrate.
        expression.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Wolverine.Persistence.IAncillaryStoreFrameProvider, PolecatAncillaryStoreFrameProvider>());

        // CritterWatch / saga-explorer diagnostic surface — Polecat
        // builds a SqlServerMessageStore underneath, so the lightweight
        // SQL Server saga storage owns every Polecat-driven saga. The
        // runtime aggregator picks this up alongside any other saga
        // storages (Marten / EF Core / RavenDB) for mixed-storage hosts.
        expression.Services.AddSingleton<Wolverine.Persistence.Sagas.ISagaStoreDiagnostics>(s =>
            new DatabaseSagaStoreDiagnostics(
                s.GetRequiredService<IWolverineRuntime>(),
                (IMessageDatabase)s.GetRequiredService<IMessageStore>()));

        // GH-3365: do NOT bridge the primary store to the store-agnostic JasperFx.Events.IEventStore
        // here. Polecat's own AddPolecat() registers IEventStore for the primary DocumentStore on every
        // overload (verified against the 4.8.0 floor of our package range), exactly the way AddMarten
        // does — which is why Wolverine.Marten registers nothing of the sort for its primary store
        // either. An earlier Polecat did not, so GH-3133 / GH-3219 added a bridge here; once Polecat
        // started registering it, the two stacked and GetServices<IEventStore>() handed back the very
        // same DocumentStore instance twice. Everything enumerating the registered stores then
        // double-counted the primary (e.g. CritterWatch's EventProgressionPoller polling it twice per
        // pass). `bootstrapping_ancillary_polecat_stores_with_wolverine` pins the resulting shape —
        // primary once, each ancillary once — so this fails loudly should Polecat ever drop its own
        // registration.
        //
        // The ancillary bridge in AncillaryWolverineOptionsPolecatExtensions IS still required:
        // AddPolecatStore<T>() does not register IEventStore for the T store.

        if (integration.UseWolverineManagedEventSubscriptionDistribution)
        {
            expression.Services.AddSingleton<WolverineProjectionCoordinator>();
            expression.Services.AddSingleton<EventSubscriptionAgentFamily>();
            expression.Services.AddSingleton<IAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
            // GH-3133, Gap 1: mirror Marten so tooling resolving GetServices<IEventSubscriptionAgentFamily>()
            // can map a shard identity to an agent URI for Polecat too.
            expression.Services.AddSingleton<IEventSubscriptionAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
            expression.Services.AddSingleton<IProjectionCoordinator, WolverineProjectionCoordinator>();
        }

        return expression;
    }

    internal static IMessageStore BuildSqlServerMessageStore(
        string schemaName,
        IDocumentStore store,
        IWolverineRuntime runtime,
        ILogger<SqlServerMessageStore> logger)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Main,
            ScheduledJobLockId = $"{schemaName}:scheduled-jobs".GetDeterministicHashCode(),
            ConnectionString = store.Options.ConnectionString
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();
        return new SqlServerMessageStore(settings, runtime.Options.Durability, logger, sagaTypes);
    }

    // GH-3445: primary-store database-per-tenant. Mirrors the ancillary
    // BuildMultiTenantedMessageDatabase<T> and the Marten twin BuildMultiTenantedMessageDatabase, but
    // with Role.Main for the master store. The master holds tenant-neutral state (nodes, assignments,
    // dead letters) and each tenant's envelope tables live in that tenant's own SQL Server database via
    // PolecatMessageDatabaseSource.
    internal static IMessageStore BuildMultiTenantedMessageStore(
        string schemaName,
        IDocumentStore store,
        IWolverineRuntime runtime,
        string? masterDatabaseConnectionString)
    {
        var connectionString = masterDatabaseConnectionString ?? store.Options.ConnectionString;

        if (connectionString.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(masterDatabaseConnectionString),
                $"Wolverine requires a main message store database even if the current Polecat tenancy model does not. Configure it via {nameof(PolecatIntegration)}.{nameof(PolecatIntegration.MainDatabaseConnectionString)} in the IntegrateWithWolverine() configuration.");
        }

        var masterSettings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Main,
            CommandQueuesEnabled = true,
            ConnectionString = connectionString
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();
        var main = new SqlServerMessageStore(masterSettings, runtime.Options.Durability,
            runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>(), sagaTypes)
        {
            Name = "Main"
        };

        var source = new PolecatMessageDatabaseSource(schemaName, AutoCreate.CreateOrUpdate, store, runtime);

        main.Initialize(runtime);

        return new MultiTenantedMessageStore(main, runtime, source);
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Polecat events at a time with
    ///     a user defined action
    /// </summary>
    public static PolecatConfigurationExpression SubscribeToEvents(
        this PolecatConfigurationExpression expression,
        IWolverineSubscription subscription)
    {
        expression.Services.SubscribeToEvents(subscription);
        return expression;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Polecat events at a time with
    ///     a user defined action
    /// </summary>
    public static IServiceCollection SubscribeToEvents(this IServiceCollection services,
        IWolverineSubscription subscription)
    {
        services.AddSingleton<IConfigurePolecat>(new SubscribeToEventsConfigurePolecat(subscription));
        return services;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Polecat events at a time with
    ///     a user defined action, resolved from the DI container
    /// </summary>
    public static PolecatConfigurationExpression SubscribeToEventsWithServices<T>(
        this PolecatConfigurationExpression expression, ServiceLifetime lifetime)
        where T : class, IWolverineSubscription
    {
        expression.Services.SubscribeToEventsWithServices<T>(lifetime);
        return expression;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Polecat events at a time with
    ///     a user defined action, resolved from the DI container
    /// </summary>
    public static IServiceCollection SubscribeToEventsWithServices<T>(this IServiceCollection services,
        ServiceLifetime lifetime)
        where T : class, IWolverineSubscription
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddSingleton<T>();
                services.AddSingleton<IConfigurePolecat>(new SubscribeToEventsWithServicesConfigurePolecat<T>(true));
                break;

            default:
                services.AddScoped<T>();
                services.AddSingleton<IConfigurePolecat>(new SubscribeToEventsWithServicesConfigurePolecat<T>(false));
                break;
        }

        return services;
    }

    /// <summary>
    ///     Create a subscription for Polecat events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Polecat</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    public static PolecatConfigurationExpression ProcessEventsWithWolverineHandlersInStrictOrder(
        this PolecatConfigurationExpression expression,
        string subscriptionName, Action<ISubscriptionOptions>? configure = null)
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    ///     Create a subscription for Polecat events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Polecat</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder(this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.AddSingleton<IConfigurePolecat>(
            new ProcessEventsInStrictOrderConfigurePolecat(subscriptionName, configure));

        return services;
    }

    /// <summary>
    ///     Relay events captured by Polecat to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Polecat</param>
    /// <param name="configure">Fine tune the relay behavior and event type filtering</param>
    public static PolecatConfigurationExpression PublishEventsToWolverine(
        this PolecatConfigurationExpression expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null)
    {
        expression.Services.PublishEventsToWolverine(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    ///     Relay events captured by Polecat to Wolverine message publishing
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Polecat</param>
    /// <param name="configure">Fine tune the relay behavior and event type filtering</param>
    public static IServiceCollection PublishEventsToWolverine(this IServiceCollection services,
        string subscriptionName, Action<IPublishingRelay>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.AddSingleton<IConfigurePolecat>(
            new PublishEventsToWolverineConfigurePolecat(subscriptionName, configure));

        return services;
    }

    internal static PolecatIntegration? FindPolecatIntegration(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(x =>
            x.ServiceType == typeof(IWolverineExtension) && x.ImplementationInstance is PolecatIntegration);

        return descriptor?.ImplementationInstance as PolecatIntegration;
    }
}

internal class SubscribeToEventsConfigurePolecat : IConfigurePolecat
{
    private readonly IWolverineSubscription _subscription;

    public SubscribeToEventsConfigurePolecat(IWolverineSubscription subscription)
    {
        _subscription = subscription;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();
        options.Projections.Subscribe(new WolverineSubscriptionRunner(_subscription, runtime));
    }
}

internal class SubscribeToEventsWithServicesConfigurePolecat<T> : IConfigurePolecat
    where T : class, IWolverineSubscription
{
    private readonly bool _isSingleton;

    public SubscribeToEventsWithServicesConfigurePolecat(bool isSingleton)
    {
        _isSingleton = isSingleton;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();

        if (_isSingleton)
        {
            var subscription = services.GetRequiredService<T>();
            options.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
        }
        else
        {
            options.Projections.Subscribe(new ScopedWolverineSubscriptionRunner<T>(services, runtime));
        }
    }
}

internal class ProcessEventsInStrictOrderConfigurePolecat : IConfigurePolecat
{
    private readonly string _subscriptionName;
    private readonly Action<ISubscriptionOptions>? _configure;

    public ProcessEventsInStrictOrderConfigurePolecat(string subscriptionName, Action<ISubscriptionOptions>? configure)
    {
        _subscriptionName = subscriptionName;
        _configure = configure;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();

        var invoker = new InlineInvoker(_subscriptionName, runtime);
        var subscription = new WolverineSubscriptionRunner(invoker, runtime);

        _configure?.Invoke(subscription);

        options.Projections.Subscribe(subscription);
    }
}

internal class PublishEventsToWolverineConfigurePolecat : IConfigurePolecat
{
    private readonly string _subscriptionName;
    private readonly Action<IPublishingRelay>? _configure;

    public PublishEventsToWolverineConfigurePolecat(string subscriptionName, Action<IPublishingRelay>? configure)
    {
        _subscriptionName = subscriptionName;
        _configure = configure;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();

        var relay = new PublishingRelay(_subscriptionName);
        _configure?.Invoke(relay);

        var subscription = new WolverineSubscriptionRunner(relay, runtime);

        options.Projections.Subscribe(subscription);
    }
}
