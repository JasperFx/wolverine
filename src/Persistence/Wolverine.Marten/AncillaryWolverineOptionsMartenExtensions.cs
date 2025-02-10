using JasperFx;
using JasperFx.Core;
using JasperFx.Core.IoC;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Internal;
using Marten.Storage;
using Marten.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Marten.Publishing;
using Wolverine.Marten.Subscriptions;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

public static class AncillaryWolverineOptionsMartenExtensions
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
    /// <param name="autoCreate">Optionally override whether to automatically create message database schema objects. Defaults to <see cref="StoreOptions.AutoCreateSchemaObjects"/>.</param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenStoreExpression<T> IntegrateWithWolverine<T>(
        this MartenServiceCollectionExtensions.MartenStoreExpression<T> expression, 
        string? schemaName = null,
        string? masterDatabaseConnectionString = null, 
        NpgsqlDataSource? masterDataSource = null, 
        AutoCreate? autoCreate = null) where T : IDocumentStore
    {
        if (schemaName.IsNotEmpty() && schemaName != schemaName.ToLowerInvariant())
        {
            throw new ArgumentOutOfRangeException(nameof(schemaName),
                "The schema name must be in all lower case characters");
        }

        expression.Services.AddSingleton<IConfigureMarten<T>, MartenOverrides<T>>();

        expression.Services.AddSingleton<IAncillaryMessageStore>(s =>
        {
            var store = s.GetRequiredService<T>().As<DocumentStore>();

            var runtime = s.GetRequiredService<IWolverineRuntime>();
            var logger = s.GetRequiredService<ILogger<PostgresqlMessageStore>>();

            schemaName ??= store.Options.DatabaseSchemaName;

            // TODO -- hacky. Need a way to expose this in Marten
            if (store.Tenancy.GetType().Name == "DefaultTenancy")
            {
                return BuildSinglePostgresqlMessageStore<T>(schemaName, autoCreate, store, runtime, logger);
            }

            return BuildMultiTenantedMessageDatabase<T>(schemaName, autoCreate, masterDatabaseConnectionString, masterDataSource, store, runtime);
        });

        expression.Services.AddType(typeof(IDatabaseSource), typeof(MartenMessageDatabaseDiscovery),
            ServiceLifetime.Singleton);
        
        // Limitation is that the wolverine objects go in the same schema
        
        expression.Services.AddSingleton<OutboxedSessionFactory<T>>();

        return expression;
    }

    internal static NpgsqlDataSource findMasterDataSource(DocumentStore store,
        DatabaseSettings masterSettings) 
    {
        if (store.Tenancy is ITenancyWithMasterDatabase m) return m.TenantDatabase.DataSource;

        if (masterSettings.DataSource != null) return (NpgsqlDataSource)masterSettings.DataSource;

        if (masterSettings.ConnectionString.IsNotEmpty()) return NpgsqlDataSource.Create(masterSettings.ConnectionString);

        throw new InvalidOperationException(
                   "There is no configured connectivity for the required master PostgreSQL message database");
    }

    internal static IAncillaryMessageStore<T> BuildMultiTenantedMessageDatabase<T>(string schemaName,
        AutoCreate? autoCreate,
        string? masterDatabaseConnectionString,
        NpgsqlDataSource? masterDataSource,
        DocumentStore store,
        IWolverineRuntime runtime) where T : IDocumentStore
    {
        var masterSettings = new DatabaseSettings
        {
            ConnectionString = masterDatabaseConnectionString,
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? store.Options.AutoCreateSchemaObjects,
            IsMaster = true,
            CommandQueuesEnabled = true,
            DataSource = masterDataSource
        };

        var dataSource = findMasterDataSource(store, masterSettings);
        var master = new PostgresqlMessageStore<T>(masterSettings, runtime.Options.Durability, dataSource,
            runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Name = "Master",
        };

        var source = new MartenMessageDatabaseSource<T>(schemaName, autoCreate ?? store.Options.AutoCreateSchemaObjects, store.As<T>(), runtime);

        master.Initialize(runtime);

        return new MultiTenantedMessageDatabase<T>(master, runtime, source);
    }

    internal static IAncillaryMessageStore<T> BuildSinglePostgresqlMessageStore<T>(
        string schemaName, 
        AutoCreate? autoCreate,
        DocumentStore store,
        IWolverineRuntime runtime, 
        ILogger<PostgresqlMessageStore> logger) where T : IDocumentStore
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName,
            AutoCreate = autoCreate ?? store.Options.AutoCreateSchemaObjects,
            IsMaster = true,
            ScheduledJobLockId = $"{schemaName ?? "public"}:scheduled-jobs".GetDeterministicHashCode()
        };

        var dataSource = store.Storage.Database.As<PostgresqlDatabase>().DataSource;

        return new PostgresqlMessageStore<T>(settings, runtime.Options.Durability, dataSource, logger);
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Marten events at a time with
    /// a user defined action
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscription"></param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenStoreExpression<T> SubscribeToEvents<T>(
        this MartenServiceCollectionExtensions.MartenStoreExpression<T> expression,
        IWolverineSubscription subscription) where T : IDocumentStore
    {
        expression.Services.SubscribeToEvents<T>(subscription);
        return expression;
    }

    /// <summary>
    /// Register a custom subscription that will process a batch of Marten events at a time with
    /// a user defined action
    /// </summary>
    /// <param name="services"></param>
    /// <param name="subscription"></param>
    /// <returns></returns>
    public static IServiceCollection SubscribeToEvents<T>(this IServiceCollection services, IWolverineSubscription subscription) where T : IDocumentStore
    {
        services.ConfigureMarten<T>((sp, opts) =>
        {
            var runtime = sp.GetRequiredService<IWolverineRuntime>();
            opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
        });

        return services;
    }
    
    /// <summary>
    /// Register a custom subscription that will process a batch of Marten events at a time with
    /// a user defined action
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="lifetime">Service lifetime of the subscription class within the application's IoC container
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenStoreExpression<T> SubscribeToEventsWithServices<T, TSubscription>(
        this MartenServiceCollectionExtensions.MartenStoreExpression<T> expression, ServiceLifetime lifetime) where TSubscription : class, IWolverineSubscription where T : IDocumentStore
    {
        expression.Services.SubscribeToEventsWithServices<T, TSubscription>(lifetime);

        return expression;
    }

    /// <summary>
    /// Add a subscription built by the IoC container to a separate Marten IDocumentStore
    /// </summary>
    /// <param name="lifetime"></param>
    /// <param name="services"></param>
    /// <typeparam name="TStore">The marker type for the separate Marten document store</typeparam>
    /// <typeparam name="TSubscription">The subscription type</typeparam>
    /// <returns></returns>
    public static IServiceCollection SubscribeToEventsWithServices<TStore, TSubscription>(this IServiceCollection services, ServiceLifetime lifetime)
        where TSubscription : class, IWolverineSubscription
        where TStore : IDocumentStore
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddSingleton<TSubscription>();
                services.ConfigureMarten<TStore>((sp, opts) =>
                {
                    var subscription = sp.GetRequiredService<TSubscription>();
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new WolverineSubscriptionRunner(subscription, runtime));
                });
                break;

            default:
                services.AddScoped<TSubscription>();
                services.ConfigureMarten<TStore>((sp, opts) =>
                {
                    var runtime = sp.GetRequiredService<IWolverineRuntime>();
                    opts.Projections.Subscribe(new ScopedWolverineSubscriptionRunner<TSubscription>(sp, runtime));
                });
                break;
        }

        return services;
    }

    /// <summary>
    /// Create a subscription for Marten events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    public static MartenServiceCollectionExtensions.MartenStoreExpression<T> ProcessEventsWithWolverineHandlersInStrictOrder<T>(
        this MartenServiceCollectionExtensions.MartenStoreExpression<T> expression,
        string subscriptionName, Action<ISubscriptionOptions>? configure = null) where T : IDocumentStore
    {
        expression.Services.ProcessEventsWithWolverineHandlersInStrictOrder<T>(subscriptionName, configure);

        return expression;
    }

    /// <summary>
    /// Create a subscription for Marten events to be processed in strict order by Wolverine
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection ProcessEventsWithWolverineHandlersInStrictOrder<T>(this IServiceCollection services,
        string subscriptionName, Action<ISubscriptionOptions>? configure) where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigureMarten<T>((sp, opts) =>
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
    /// Relay events captured by Marten to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static MartenServiceCollectionExtensions.MartenStoreExpression<T> PublishEventsToWolverine<T>(
        this MartenServiceCollectionExtensions.MartenStoreExpression<T> expression,
        string subscriptionName, Action<IPublishingRelay>? configure = null) where T : IDocumentStore
    {
        expression.Services.PublishEventsToWolverine<T>(subscriptionName, configure);

        return expression;
    }

    /// <summary>
    /// Relay events captured by Marten to Wolverine message publishing
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="subscriptionName">Descriptive name for this event subscription for tracking with Marten</param>
    /// <param name="configure">Fine tune the asynchronous daemon behavior of this subscription</param>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection PublishEventsToWolverine<T>(this IServiceCollection services, string subscriptionName, Action<IPublishingRelay>? configure) where T : IDocumentStore
    {
        if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
        services.ConfigureMarten<T>((sp, opts) =>
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