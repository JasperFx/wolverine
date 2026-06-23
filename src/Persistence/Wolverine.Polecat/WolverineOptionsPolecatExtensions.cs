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

        // GH-3133 / GH-3219, Gap 2: Polecat's AddPolecat registers IDocumentStore but not the
        // store-agnostic JasperFx.Events.IEventStore (the Polecat DocumentStore implements
        // IEventStore<IDocumentSession, IQuerySession>). Bridge it UNCONDITIONALLY so the store is
        // discoverable via GetServices<IEventStore>() regardless of whether managed distribution is
        // enabled — the EventSubscriptionAgentFamily resolves stores this way (when managed distribution
        // is on) AND so does the read-only capabilities / CritterWatch projection-explorer surface
        // (always). Marten registers IEventStore unconditionally in its own AddMarten.
        expression.Services.AddSingleton<IEventStore>(s => (IEventStore)s.GetRequiredService<IDocumentStore>());

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
