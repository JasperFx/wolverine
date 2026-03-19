using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
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

            var schemaName = integration.MessageStorageSchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             "wolverine";

            return BuildSqlServerMessageStore(schemaName, store, runtime, logger);
        });

        expression.Services.AddSingleton<IConfigurePolecat, PolecatOverrides>();

        expression.Services.AddSingleton<OutboxedSessionFactory>();

        if (integration.UseWolverineManagedEventSubscriptionDistribution)
        {
            expression.Services.AddSingleton<WolverineProjectionCoordinator>();
            expression.Services.AddSingleton<EventSubscriptionAgentFamily>();
            expression.Services.AddSingleton<IAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
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
