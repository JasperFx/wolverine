using JasperFx;
using JasperFx.Core;
using JasperFx.Core.IoC;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.Subscriptions;
using Marten;
using Marten.Events.Daemon.Coordination;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Marten.Distribution;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Marten.Publishing;
using Wolverine.Marten.Subscriptions;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Postgresql.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Marten;

internal class MapEventTypeMessages : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.MapGenericMessageType(typeof(IEvent<>), typeof(Event<>));
    }
}

public static class WolverineOptionsMartenExtensions
{
    /// <summary>
    ///     Integrate Marten with Wolverine's persistent outbox and add Marten-specific middleware
    ///     to Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="schemaName">Optionally move the Wolverine envelope storage to a separate schema</param>
    /// <param name="masterDataSource">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </param>
    /// <param name="masterDatabaseConnectionString">
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </param>
    /// <param name="transportSchemaName">Optionally configure the schema name for any PostgreSQL queues</param>
    /// <param name="autoCreate">
    ///     Optionally override whether to automatically create message database schema objects. Defaults
    ///     to <see cref="StoreOptions.AutoCreateSchemaObjects" />.
    /// </param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression IntegrateWithWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression,
        Action<MartenIntegration>? configure = null)
    {
        var integration = expression.Services.FindMartenIntegration();
        if (integration == null)
        {
            integration = new MartenIntegration();

            configure?.Invoke(integration);

            expression.Services.AddSingleton(integration);
            expression.Services.AddSingleton<IWolverineExtension>(integration);
        }
        else
        {
            configure?.Invoke(integration);
        }

        expression.Services.AddSingleton<Migrator, PostgresqlMigrator>();

        expression.Services.AddSingleton<IWolverineExtension, MapEventTypeMessages>();

        expression.Services.AddScoped<IMartenOutbox, MartenOutbox>();

        // GH-3001: structural scope priming for Marten sessions. When a handler falls back to service
        // location, the generated code primes the child scope's ScopedDocumentSessionHolder with the
        // handler's outbox-enrolled IDocumentSession (PrimeScopedDocumentSessionFrame). Decorate
        // Marten's own IDocumentSession / IQuerySession scoped registrations so service-located
        // resolution prefers that primed session — enrolled with the active outbox — instead of a
        // separate, un-enrolled session. Non-handler scopes (the holder is empty) fall back to
        // Marten's original session factory.
        expression.Services.AddScoped<ScopedDocumentSessionHolder>();
        expression.Services.PreferPrimedSession<IDocumentSession>(primedSession);
        expression.Services.PreferPrimedSession<IQuerySession>(primedSession);

        // GH-3956: Marten registers its own interfaces, never the store-agnostic JasperFx.Events.Documents
        // contracts its session types already implement, so a service depending on one of those could not
        // be resolved at all on a stock host. Handler PARAMETERS are satisfied by codegen
        // (SharedDocumentOperationsSource); these registrations cover everything codegen does not see --
        // a service-located contract, or one injected into a class that a handler depends on.
        //
        // Deliberately delegating to IDocumentSession / IQuerySession rather than to a fresh session, so
        // the PreferPrimedSession decoration above applies: inside a handler scope these resolve to the
        // handler's outbox-enrolled session, not a separate un-enrolled one.
        expression.Services.TryAddScoped<IDocumentSessionOperations>(s => s.GetRequiredService<IDocumentSession>());
        expression.Services.TryAddScoped<IDocumentWriteOperations>(s => s.GetRequiredService<IDocumentSession>());
        expression.Services.TryAddScoped<IDocumentReadOperations>(s => s.GetRequiredService<IQuerySession>());

        // IDocumentStore implements both of these, and it is a singleton, so this is a straight alias.
        // Same move jasperfx#430 made for IProjectionCoordinator (see line ~149 below).
        expression.Services.TryAddSingleton<IDocumentSessionFactory>(s => s.GetRequiredService<IDocumentStore>());
        expression.Services
            .TryAddSingleton<IDocumentSessionFactory<IDocumentSession, IQuerySession>>(s =>
                (IDocumentSessionFactory<IDocumentSession, IQuerySession>)s.GetRequiredService<IDocumentStore>());

        // GH-4044. Conjoined EF Core tenant partitioning finds its provider through this factory, and
        // PersistMessagesWithPostgresql() is the only other thing that registers it -- which an
        // application letting Marten own the message store never calls
        expression.Services.TryAddEnumerable(ServiceDescriptor
            .Singleton<ITenantPartitioningProviderFactory, PostgresqlTenantPartitioningProviderFactory>());

        // Gotta have at least a placeholder just in case a user also has
        // EF Core
        expression.Services.AddSingleton<DatabaseSettings>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>() as IMessageDatabase;
            if (store != null) return store.Settings;

            return new DatabaseSettings();
        });

        expression.Services.AddSingleton<IMessageStore>(s =>
        {
            var store = s.GetRequiredService<IDocumentStore>().As<DocumentStore>();

            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<PostgresqlMessageStore>>();

            var schemaName = integration.MessageStorageSchemaName ??
                             runtime.Options.Durability.MessageStorageSchemaName ??
                             store.Options.DatabaseSchemaName ?? "public";


            if (store.Tenancy.Cardinality == DatabaseCardinality.Single)
            {
                return BuildSinglePostgresqlMessageStore(schemaName, integration.AutoCreate, store, runtime, logger);
            }

            var masterDatabaseConnectionString = integration.MainDatabaseConnectionString;
            var masterDataSource = integration.MasterDataSource;

            if (store.Tenancy is MasterTableTenancy masterTableTenancy)
            {
                masterDataSource = masterTableTenancy.TenantDatabase.DataSource;
            }
            
            return BuildMultiTenantedMessageDatabase(schemaName, integration.AutoCreate,
                masterDatabaseConnectionString, masterDataSource, store, runtime, s);
        });

        if (integration.UseWolverineManagedEventSubscriptionDistribution)
        {
            expression.Services.AddSingleton<WolverineProjectionCoordinator>();
            expression.Services.AddSingleton<EventSubscriptionAgentFamily>();
            expression.Services.AddSingleton<IAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
            expression.Services.AddSingleton<IEventSubscriptionAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
            expression.Services.AddSingleton<IProjectionCoordinator, WolverineProjectionCoordinator>();

            // GH-3388 — refuse a competing Marten-side daemon (Solo/HotCold) at host start, where
            // the store's options are final regardless of the order AddAsyncDaemon() was called in.
            expression.Services.AddSingleton<IHostedService, ManagedDistributionDaemonModeValidator>();
        }

        expression.Services.AddType(typeof(IDatabaseSource), typeof(MessageDatabaseDiscovery),
            ServiceLifetime.Singleton);

        expression.Services.AddSingleton<IConfigureMarten, MartenOverrides>();

        expression.Services.AddSingleton<OutboxedSessionFactory>();

        // GH-3109: lets the provider-agnostic [Storage(typeof(IMyStore))] attribute route a handler to
        // a Marten ancillary store by resolving this provider from the store marker type. Registered
        // here (not in MartenIntegration.Configure) so the singleton is present in the codegen-time
        // container that StorageAttribute.Modify queries. TryAddEnumerable keeps it to one instance
        // even when multiple Marten stores integrate.
        expression.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Wolverine.Persistence.IAncillaryStoreFrameProvider, MartenAncillaryStoreFrameProvider>());

        // CritterWatch / saga-explorer diagnostic surface — Marten owns
        // every saga whose state class is a Marten document, so register
        // a Marten-backed ISagaStoreDiagnostics that the runtime
        // aggregator fans out to.
        expression.Services.AddSingleton<ISagaStoreDiagnostics>(s =>
            new MartenSagaStoreDiagnostics(
                s.GetRequiredService<IWolverineRuntime>(),
                s.GetRequiredService<IDocumentStore>()));

        return expression;
    }

    // GH-3001: the scope-primed session (the outbox-enrolled session the handler is using), or null
    // outside a handler scope -- where PreferPrimedSession falls back to Marten's own session factory.
    private static object? primedSession(IServiceProvider services)
    {
        return services.GetRequiredService<ScopedDocumentSessionHolder>().Session;
    }

    internal static NpgsqlDataSource findMasterDataSource(
        DocumentStore store,
        IWolverineRuntime runtime,
        DatabaseSettings masterSettings,
        IServiceProvider container)
    {
        if (store.Tenancy is ITenancyWithMasterDatabase m)
        {
            return m.TenantDatabase.DataSource;
        }

        if (masterSettings.DataSource != null)
        {
            return (NpgsqlDataSource)masterSettings.DataSource;
        }

        if (masterSettings.ConnectionString.IsNotEmpty())
        {
            return NpgsqlDataSource.Create(masterSettings.ConnectionString);
        }

        var source = container.GetService<NpgsqlDataSource>();

        return source ??
               throw new InvalidOperationException(
                   "There is no configured connectivity for the required master PostgreSQL message database");
    }

    internal static IMessageStore BuildMultiTenantedMessageDatabase(
        string schemaName,
        AutoCreate? autoCreate,
        string? masterDatabaseConnectionString,
        NpgsqlDataSource? masterDataSource,
        DocumentStore store,
        IWolverineRuntime runtime,
        IServiceProvider serviceProvider)
    {
        if (masterDataSource == null && masterDatabaseConnectionString.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(masterDatabaseConnectionString),
                $"Wolverine requires a main message store database even if the current Marten tenancy model does not. You may need to explicitly configure that in the {nameof(IntegrateWithWolverine)}() configuration.");
        }
        
        var masterSettings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? store.Options.AutoCreateSchemaObjects,
            Role = MessageStoreRole.Main,
            CommandQueuesEnabled = true,
            DataSource = masterDataSource ?? NpgsqlDataSource.Create(masterDatabaseConnectionString!)
        };

        var dataSource = findMasterDataSource(store, runtime, masterSettings, serviceProvider);
        var main = new PostgresqlMessageStore(masterSettings, runtime.Options.Durability, dataSource,
            runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Name = "Main"
        };


        var source = new MartenMessageDatabaseSource(schemaName, autoCreate ?? store.Options.AutoCreateSchemaObjects,
            store, runtime);

        main.Initialize(runtime);

        return new MultiTenantedMessageStore(main, runtime, source);
    }

    internal static IMessageStore BuildSinglePostgresqlMessageStore(
        string schemaName,
        AutoCreate? autoCreate,
        DocumentStore store,
        IWolverineRuntime runtime,
        ILogger<PostgresqlMessageStore> logger)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? store.Options.AutoCreateSchemaObjects,
            Role = MessageStoreRole.Main,
            ScheduledJobLockId = $"{schemaName ?? "public"}:scheduled-jobs".GetDeterministicHashCode()
        };

        var dataSource = store.Storage.Database.As<PostgresqlDatabase>().DataSource;

        return new PostgresqlMessageStore(settings, runtime.Options.Durability, dataSource, logger);
    }

    internal static MartenIntegration? FindMartenIntegration(this IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(x =>
            x.ServiceType == typeof(IWolverineExtension) && x.ImplementationInstance is MartenIntegration);

        return descriptor?.ImplementationInstance as MartenIntegration;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Marten events at a time with
    ///     a user defined action
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscription"></param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression SubscribeToEvents(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression,
        IWolverineSubscription subscription)
    {
        expression.Services.SubscribeToEvents(subscription);
        return expression;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Marten events at a time with
    ///     a user defined action
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscription"></param>
    /// <returns></returns>
    public static IServiceCollection SubscribeToEvents(this IServiceCollection services,
        IWolverineSubscription subscription)
    {
        services.ConfigureMarten((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();
            opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
        });

        return services;
    }

    /// <summary>
    ///     Register a custom subscription that will process a batch of Marten events at a time with
    ///     a user defined action
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="lifetime">
    ///     Service lifetime of the subscription class within the application's IoC container
    ///     <returns></returns>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression SubscribeToEventsWithServices<T>(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression, ServiceLifetime lifetime)
        where T : class, IWolverineSubscription
    {
        expression.Services.SubscribeToEventsWithServices<T>(lifetime);

        return expression;
    }

    /// <summary>
    ///     <param name="expression"></param>
    ///     <param name="lifetime">Service lifetime of the subscription class within the application's IoC container
    /// </summary>
    /// <param name="lifetime"></param>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection SubscribeToEventsWithServices<T>(this IServiceCollection services,
        ServiceLifetime lifetime)
        where T : class, IWolverineSubscription
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddSingleton<T>();
                services.ConfigureMarten((sp, opts) =>
                {
                    var subscription = sp.GetRequiredService<T>();
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
                });
                break;

            default:
                services.AddScoped<T>();
                services.ConfigureMarten((sp, opts) =>
                {
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new ScopedWolverineSubscriptionRunner<T>(sp, runtime));
                });
                break;
        }

        return services;
    }

    /// <summary>
    ///     Create a subscription for Marten events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression
        ProcessEventsWithWolverineHandlersInStrictOrder(
            this MartenServiceCollectionExtensions.MartenConfigurationExpression expression,
            string subscriptionName, Action<ISubscriptionOptions>? configure = null)
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder(subscriptionName, configure);

        return expression;
    }

    /// <summary>
    ///     Create a subscription for Marten events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder(this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.ConfigureMarten((sp, opts) =>
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
    ///     Relay events captured by Marten to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression PublishEventsToWolverine(
        this MartenServiceCollectionExtensions.MartenConfigurationExpression expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null)
    {
        expression.Services.PublishEventsToWolverine(subscriptionName, configure);

        return expression;
    }

    /// <summary>
    ///     Relay events captured by Marten to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection PublishEventsToWolverine(this IServiceCollection services, string subscriptionName,
        Action<IPublishingRelay>? configure)
    {
        if (subscriptionName.IsEmpty())
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        services.ConfigureMarten((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();

            var relay = new PublishingRelay(subscriptionName, opts.Events.TenancyStyle);
            configure?.Invoke(relay);

            var subscription = new WolverineSubscriptionRunner(relay, runtime);

            opts.Projections.Subscribe(subscription);
        });

        return services;
    }
}