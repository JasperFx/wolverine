using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wolverine.Fisher.Publishing;
using Wolverine.Fisher.Subscriptions;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Fisher;

internal class MapEventTypeMessages : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.MapGenericMessageType(typeof(IEvent<>), typeof(Event<>));
    }
}

public static class WolverineOptionsFisherExtensions
{
    /// <summary>
    ///     Integrate Fisher with Wolverine's persistent outbox and add Fisher-specific middleware
    ///     to Wolverine
    /// </summary>
    public static FisherConfigurationExpression IntegrateWithWolverine(
        this FisherConfigurationExpression expression,
        Action<FisherIntegration>? configure = null)
    {
        var integration = expression.Services.FindFisherIntegration();
        if (integration == null)
        {
            integration = new FisherIntegration();

            configure?.Invoke(integration);

            expression.Services.AddSingleton(integration);
            expression.Services.AddSingleton<IWolverineExtension>(integration);
        }
        else
        {
            configure?.Invoke(integration);
        }

        expression.Services.AddSingleton<IWolverineExtension, MapEventTypeMessages>();

        expression.Services.AddScoped<IFisherOutbox, FisherOutbox>();

        // GH-3956: Fisher registers its own interfaces, never the store-agnostic
        // JasperFx.Events.Documents contracts its session types already implement, so a service depending
        // on one of those could not be resolved at all on a stock host. Handler PARAMETERS are satisfied by
        // codegen (SharedDocumentOperationsSource); these registrations cover everything codegen does not
        // see -- a service-located contract, or one injected into a class that a handler depends on.
        expression.Services.TryAddScoped<IDocumentSessionOperations>(s => s.GetRequiredService<IDocumentSession>());
        expression.Services.TryAddScoped<IDocumentWriteOperations>(s => s.GetRequiredService<IDocumentSession>());
        expression.Services.TryAddScoped<IDocumentReadOperations>(s => s.GetRequiredService<IQuerySession>());

        // IDocumentStore implements both of these, and it is a singleton, so this is a straight alias.
        expression.Services.TryAddSingleton<IDocumentSessionFactory>(s => s.GetRequiredService<IDocumentStore>());
        expression.Services
            .TryAddSingleton<IDocumentSessionFactory<IDocumentSession, IQuerySession>>(s =>
                (IDocumentSessionFactory<IDocumentSession, IQuerySession>)s.GetRequiredService<IDocumentStore>());

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
            var logger = s.GetRequiredService<ILogger<SqliteMessageStore>>();

            // "main", not "wolverine". SQLite has no user-defined schemas: the only schema names a
            // plain connection knows are "main", "temp", and whatever has been ATTACHed. The Marten
            // and Polecat twins default to "wolverine" and inherit the store's DatabaseSchemaName,
            // both of which produce "wolverine.wolverine_incoming_envelopes" here and fail with
            // "no such table". Wolverine.Sqlite's own default is "main" for exactly this reason.
            var schemaName = integration.MessageStorageSchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             "main";

            // GH-3907: no database-per-tenant branch here, unlike the Marten and Polecat twins. A
            // Fisher store IS a SQLite file, so its multi-tenancy is a file per tenant rather than a
            // schema or database per tenant, and Wolverine's durability tables cannot follow a tenant
            // across files without a second writer per file — the one thing fisher#68 says not to do.
            // Tenanted Fisher hosts are tracked separately; the single-file shape is what this release
            // supports.
            return BuildSqliteMessageStore(schemaName, store, runtime, logger);
        });

        expression.Services.AddSingleton<IConfigureFisher, FisherOverrides>();

        expression.Services.AddSingleton<OutboxedSessionFactory>();

        // GH-3109: lets the provider-agnostic [Storage(typeof(IMyStore))] attribute route a handler to
        // a Fisher ancillary store by resolving this provider from the store marker type. Registered
        // here (not in FisherIntegration.Configure) so the singleton is present in the codegen-time
        // container that StorageAttribute.Modify queries. TryAddEnumerable keeps it to one instance
        // even when several Fisher stores integrate.
        expression.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Wolverine.Persistence.IAncillaryStoreFrameProvider, FisherAncillaryStoreFrameProvider>());

        // CritterWatch / saga-explorer diagnostic surface — Fisher
        // builds a SqliteMessageStore underneath, so the lightweight
        // SQLite saga storage owns every Fisher-driven saga. The
        // runtime aggregator picks this up alongside any other saga
        // storages (Marten / EF Core / RavenDB) for mixed-storage hosts.
        expression.Services.AddSingleton<Wolverine.Persistence.Sagas.ISagaStoreDiagnostics>(s =>
            new DatabaseSagaStoreDiagnostics(
                s.GetRequiredService<IWolverineRuntime>(),
                (IMessageDatabase)s.GetRequiredService<IMessageStore>()));

        // GH-3365: do NOT bridge the primary store to the store-agnostic JasperFx.Events.IEventStore
        // here. Fisher's own AddFisher() registers IEventStore for the primary DocumentStore on every
        // overload, exactly the way AddMarten does - which is why Wolverine.Marten registers nothing
        // of the sort for its primary store either. Registering a second one makes
        // GetServices<IEventStore>() hand back the very same DocumentStore twice, and everything
        // enumerating the registered stores then double-counts the primary. The Marten and Polecat
        // integrations each learned this the expensive way (GH-3133 / GH-3219); Fisher inherits the
        // conclusion rather than the detour.

        // No Wolverine-managed subscription distribution here, unlike the Marten and Polecat twins.
        // Distributing subscriptions across nodes presupposes several nodes sharing one event store,
        // and a Fisher store is a single SQLite file - Fisher itself refuses DaemonMode.HotCold for
        // the same reason. Run the daemon in Solo mode via Fisher's own AddAsyncDaemon().

        return expression;
    }

    internal static IMessageStore BuildSqliteMessageStore(
        string schemaName,
        IDocumentStore store,
        IWolverineRuntime runtime,
        ILogger<SqliteMessageStore> logger)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            // GH-3943: SQLite has no schemas, so the name is a table name prefix. Without this the
            // store emits `<name>.wolverine_incoming_envelopes` against a database nothing ATTACHed
            // and the host dies on the first envelope write with "no such table".
            SchemaNameIsTablePrefix = true,
            AutoCreate = AutoCreate.CreateOrUpdate,
            Role = MessageStoreRole.Main,
            ScheduledJobLockId = $"{schemaName}:scheduled-jobs".GetDeterministicHashCode(),
            ConnectionString = store.Options.ConnectionString
        };

        var sagaTypes = runtime.Services.GetServices<SagaTableDefinition>();

        // Wolverine.Sqlite's store takes a DbDataSource rather than a connection string - one pooled
        // source per file, which is also what keeps Wolverine's own durability traffic to a single
        // writer against that file.
        var dataSource = new WolverineSqliteDataSource(store.Options.ConnectionString);

        return new SqliteMessageStore(settings, runtime.Options.Durability, dataSource, logger, sagaTypes);
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Fisher events at a time with
    ///     a user defined action
    /// </summary>
    public static FisherConfigurationExpression SubscribeToEvents(
        this FisherConfigurationExpression expression,
        IWolverineSubscription subscription)
    {
        expression.Services.SubscribeToEvents(subscription);
        return expression;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Fisher events at a time with
    ///     a user defined action
    /// </summary>
    public static IServiceCollection SubscribeToEvents(this IServiceCollection services,
        IWolverineSubscription subscription)
    {
        services.AddSingleton<IConfigureFisher>(new SubscribeToEventsConfigureFisher(subscription));
        return services;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Fisher events at a time with
    ///     a user defined action, resolved from the DI container
    /// </summary>
    public static FisherConfigurationExpression SubscribeToEventsWithServices<T>(
        this FisherConfigurationExpression expression, ServiceLifetime lifetime)
        where T : class, IWolverineSubscription
    {
        expression.Services.SubscribeToEventsWithServices<T>(lifetime);
        return expression;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Fisher events at a time with
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
                services.AddSingleton<IConfigureFisher>(new SubscribeToEventsWithServicesConfigureFisher<T>(true));
                break;

            default:
                services.AddScoped<T>();
                services.AddSingleton<IConfigureFisher>(new SubscribeToEventsWithServicesConfigureFisher<T>(false));
                break;
        }

        return services;
    }

    /// <summary>
    ///     Create a subscription for Fisher events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Fisher</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    public static FisherConfigurationExpression ProcessEventsWithWolverineHandlersInStrictOrder(
        this FisherConfigurationExpression expression,
        string subscriptionName, Action<ISubscriptionOptions>? configure = null)
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    ///     Create a subscription for Fisher events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Fisher</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder(this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.AddSingleton<IConfigureFisher>(
            new ProcessEventsInStrictOrderConfigureFisher(subscriptionName, configure));

        return services;
    }

    /// <summary>
    ///     Relay events captured by Fisher to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Fisher</param>
    /// <param name="configure">Fine tune the relay behavior and event type filtering</param>
    public static FisherConfigurationExpression PublishEventsToWolverine(
        this FisherConfigurationExpression expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null)
    {
        expression.Services.PublishEventsToWolverine(subscriptionName, configure);
        return expression;
    }

    /// <summary>
    ///     Relay events captured by Fisher to Wolverine message publishing
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Fisher</param>
    /// <param name="configure">Fine tune the relay behavior and event type filtering</param>
    public static IServiceCollection PublishEventsToWolverine(this IServiceCollection services,
        string subscriptionName, Action<IPublishingRelay>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.AddSingleton<IConfigureFisher>(
            new PublishEventsToWolverineConfigureFisher(subscriptionName, configure));

        return services;
    }

    internal static FisherIntegration? FindFisherIntegration(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(x =>
            x.ServiceType == typeof(IWolverineExtension) && x.ImplementationInstance is FisherIntegration);

        return descriptor?.ImplementationInstance as FisherIntegration;
    }
}

internal class SubscribeToEventsConfigureFisher : IConfigureFisher
{
    private readonly IWolverineSubscription _subscription;

    public SubscribeToEventsConfigureFisher(IWolverineSubscription subscription)
    {
        _subscription = subscription;
    }

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();
        options.Projections.Subscribe(new WolverineSubscriptionRunner(_subscription, runtime));
    }
}

internal class SubscribeToEventsWithServicesConfigureFisher<T> : IConfigureFisher
    where T : class, IWolverineSubscription
{
    private readonly bool _isSingleton;

    public SubscribeToEventsWithServicesConfigureFisher(bool isSingleton)
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

internal class ProcessEventsInStrictOrderConfigureFisher : IConfigureFisher
{
    private readonly string _subscriptionName;
    private readonly Action<ISubscriptionOptions>? _configure;

    public ProcessEventsInStrictOrderConfigureFisher(string subscriptionName, Action<ISubscriptionOptions>? configure)
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

internal class PublishEventsToWolverineConfigureFisher : IConfigureFisher
{
    private readonly string _subscriptionName;
    private readonly Action<IPublishingRelay>? _configure;

    public PublishEventsToWolverineConfigureFisher(string subscriptionName, Action<IPublishingRelay>? configure)
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
