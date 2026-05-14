using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weasel.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.EntityFrameworkCore.Internals.Migrations;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

public static class WolverineEntityCoreExtensions
{
    internal const string WolverineEnabled = "WolverineEnabled";

    /// <summary>
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <param name="wolverineDatabaseSchema"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddDbContextWithWolverineIntegration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
    {
        return addDbContextWithWolverineIntegration<T>(services, (_, b) => configure(b), wolverineDatabaseSchema);
     }

    /// <summary>
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <param name="wolverineDatabaseSchema"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddDbContextWithWolverineIntegration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(this IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
    {
        return addDbContextWithWolverineIntegration<T>(services, configure, wolverineDatabaseSchema);
    }

    /// <summary>
    /// Register a DbContext type that should use the separately configured Wolverine managed multi-tenancy
    /// for separate databases per tenant
    /// </summary>
    /// <param name="services"></param>
    /// <param name="dbContextConfiguration"></param>
    /// <param name="autoCreate">Should this application try to create missing databases and apply missing migrations at application startup? Default is None, all other options will create the databases.</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static IServiceCollection AddDbContextWithWolverineManagedMultiTenancy<T>(this IServiceCollection services,
        Action<DbContextOptionsBuilder<T>, ConnectionString, TenantId> dbContextConfiguration, AutoCreate autoCreate = AutoCreate.None) where T : DbContext
    {
        services.TryAddSingleton<IDbContextOutboxFactory, DbContextOutboxFactory>();
        registerEFCoreSagaStoreDiagnostics(services);

        // For code generation
        services.AddSingleton<IWolverineExtension, EntityFrameworkCoreBackedPersistence<T>>();
        
        // STRICTLY FOR EF CORE MIGRATIONS!!!!
        services.AddScoped<T>(s =>
        {
            return (T)s.GetRequiredService<IDbContextBuilder<T>>().BuildForMain();
        });

        services.AddSingleton<DbContextOptions<T>>(s =>
        {
            var builder = s.GetRequiredService<IDbContextBuilder<T>>();
            return builder.BuildOptionsForMain();
        });
        
        services.AddSingleton<IDbContextBuilder<T>>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>();
            var tenanted = store as MultiTenantedMessageStore;
            if (tenanted == null || tenanted.Main is not IMessageDatabase)
            {
                throw new InvalidOperationException(
                    $"Configured multi-tenanted usage of {typeof(T).FullNameInCode()} requires multi-tenanted Wolverine database storage");
            }

            return new TenantedDbContextBuilderByConnectionString<T>(s, tenanted, dbContextConfiguration, s.GetServices<IDomainEventScraper>());
        });

        services.AddSingleton<IDbContextBuilder>(s => s.GetRequiredService<IDbContextBuilder<T>>());

        // CritterWatch (#102): per-tenant snapshot, masked to server / database
        // / tenantId only — never the raw connection string.
        services.AddSingleton<IDbContextUsageSource, TenantedDbContextUsageSource<T>>();

        if (autoCreate != AutoCreate.None)
        {
            services.AddSingleton<IResourceCreator, TenantedDbContextInitializer<T>>();
        }

        return services;
    }

    /// <summary>
    /// Register a DbContext type that should use the separately configured Wolverine managed multi-tenancy
    /// for separate databases per tenant using DbDataSource. This option is necessary when using EF Core *with*
    /// Marten managed Multi-Tenancy
    /// </summary>
    /// <param name="services"></param>
    /// <param name="dbContextConfiguration"></param>
    /// <param name="autoCreate">Should this application try to create missing databases and apply missing migrations at application startup? Default is None, all other options will create the databases.</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static IServiceCollection AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<T>(this IServiceCollection services,
        Action<DbContextOptionsBuilder<T>, DbDataSource, TenantId> dbContextConfiguration, AutoCreate autoCreate = AutoCreate.None) where T : DbContext
    {
        services.TryAddSingleton<IDbContextOutboxFactory, DbContextOutboxFactory>();
        registerEFCoreSagaStoreDiagnostics(services);

        // For code generation
        services.AddSingleton<IWolverineExtension, EntityFrameworkCoreBackedPersistence<T>>();
        
        // STRICTLY FOR EF CORE MIGRATIONS!!!!
        services.AddScoped<T>(s =>
        {
            return (T)s.GetRequiredService<IDbContextBuilder<T>>().BuildForMain();
        });

        services.AddSingleton<DbContextOptions<T>>(s =>
        {
            var builder = s.GetRequiredService<IDbContextBuilder<T>>();
            return builder.BuildOptionsForMain();
        });
        
        services.AddSingleton<IDbContextBuilder<T>>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>();
            var tenanted = store as MultiTenantedMessageStore;
            if (tenanted == null || tenanted.Main is not IMessageDatabase)
            {
                throw new InvalidOperationException(
                    $"Configured multi-tenanted usage of {typeof(T).FullNameInCode()} requires multi-tenanted Wolverine database storage");
            }

            return new TenantedDbContextBuilderByDbDataSource<T>(s, tenanted, dbContextConfiguration, s.GetServices<IDomainEventScraper>());
        });

        services.AddSingleton<IDbContextBuilder>(s => s.GetRequiredService<IDbContextBuilder<T>>());

        // CritterWatch (#102): per-tenant snapshot via DbDataSource shares the
        // same masked descriptor shape as the connection-string variant.
        services.AddSingleton<IDbContextUsageSource, TenantedDbContextUsageSource<T>>();

        if (autoCreate != AutoCreate.None)
        {
            services.AddSingleton<IResourceCreator, TenantedDbContextInitializer<T>>();
        }

        return services;
    }


    // EF Core's AddDbContext<T> requires DAM(PublicConstructors) on T to satisfy
    // its own internal reflection. Forward the annotation up so callers (e.g.
    // AddWolverineEFCore<T>) get the cascade and concrete-type registration
    // sites satisfy it automatically.
    private static IServiceCollection addDbContextWithWolverineIntegration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
    {
        services.TryAddSingleton<IDbContextOutboxFactory, DbContextOutboxFactory>();
        registerEFCoreSagaStoreDiagnostics(services);

        services.AddDbContext<T>((s, b) =>
        {
            configure(s, b);
            b.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

        services.TryAddSingleton<IWolverineExtension, EntityFrameworkCoreBackedPersistence>();

        services.TryAddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
        services.TryAddScoped<IDbContextOutbox, DbContextOutbox>();

        // CritterWatch (#102): expose this DbContext to the descriptor pipeline
        // so the Storage tab can render its model + Wolverine integration shape.
        services.AddSingleton<IDbContextUsageSource, DbContextUsageSource<T>>();

        return services;
    }

    /// <summary>
    /// Marker registration used by <see cref="registerEFCoreSagaStoreDiagnostics"/>
    /// to detect whether this extension has already added its
    /// <see cref="ISagaStoreDiagnostics"/> contribution. We can't use
    /// <c>TryAddSingleton&lt;ISagaStoreDiagnostics&gt;</c> for that gate because
    /// the runtime aggregator (<c>AggregateSagaStoreDiagnostics</c>) is a
    /// fan-out: SqlServer / Postgres / Marten / EF Core / RavenDB each
    /// register their own <c>ISagaStoreDiagnostics</c> additively, and
    /// <c>TryAdd</c> would silently drop the EF Core one whenever a
    /// lightweight RDBMS provider was wired up first. See #2735 and the
    /// fan-out comment on <c>AggregateSagaStoreDiagnostics</c>.
    /// </summary>
    private sealed class EFCoreSagaStoreDiagnosticsRegistered;

    /// <summary>
    /// Registers the EF Core <see cref="ISagaStoreDiagnostics"/> for the
    /// CritterWatch / saga-explorer fan-out (the runtime aggregator iterates
    /// over every <see cref="ISagaStoreDiagnostics"/> registered in DI).
    ///
    /// **Why it lives here and not in <see cref="EntityFrameworkCoreBackedPersistence"/>.Configure**:
    /// the EF Core extension is registered into DI as <c>IWolverineExtension</c>
    /// at every entry point that wires a <see cref="DbContext"/> for Wolverine
    /// (see <see cref="AddDbContextWithWolverineManagedMultiTenancy{T}"/> et al).
    /// That means the extension's <c>Configure</c> runs at host-build time
    /// from inside the <c>AddSingleton</c> lambda in <c>HostBuilderExtensions</c> —
    /// at which point <see cref="IServiceCollection"/> is already read-only and
    /// any <c>options.Services.Add*</c> call throws <c>InvalidOperationException</c>.
    /// Wolverine's 3.0+ policy then re-throws that as the explicit
    /// "no longer supported to alter IoC service registrations through Wolverine
    /// extensions that are themselves registered in the IoC container" message.
    /// Closes wolverine#2735.
    ///
    /// Idempotent via the <see cref="EFCoreSagaStoreDiagnosticsRegistered"/>
    /// marker — every entry point can call this safely, and only the first
    /// call adds the fan-out registration. Note that we deliberately use
    /// <c>AddSingleton</c> (not <c>TryAddSingleton</c>) on the
    /// <see cref="ISagaStoreDiagnostics"/> registration itself: lightweight
    /// RDBMS providers (SqlServer / Postgres) and document providers
    /// (Marten / RavenDB) register their own <see cref="ISagaStoreDiagnostics"/>
    /// additively, and the runtime aggregator fans out across all of them.
    /// </summary>
    private static void registerEFCoreSagaStoreDiagnostics(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(EFCoreSagaStoreDiagnosticsRegistered)))
        {
            return;
        }

        services.AddSingleton<EFCoreSagaStoreDiagnosticsRegistered>();
        services.AddSingleton<ISagaStoreDiagnostics>(s =>
            new EFCoreSagaStoreDiagnostics(
                s.GetRequiredService<IWolverineRuntime>(),
                s));
    }

    /// <summary>
    ///     Uses Entity Framework Core for Saga persistence and transactional
    ///     middleware using <see cref="TransactionMiddlewareMode.Eager"/> mode by default.
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCoreTransactions(this WolverineOptions options)
    {
        options.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
    }

    /// <summary>
    ///     Uses Entity Framework Core for Saga persistence and transactional
    ///     middleware with the specified <see cref="TransactionMiddlewareMode"/>.
    ///     <see cref="TransactionMiddlewareMode.Eager"/> opens an explicit database transaction immediately.
    ///     <see cref="TransactionMiddlewareMode.Lightweight"/> only relies on <c>DbContext.SaveChangesAsync()</c>
    ///     without opening an explicit transaction.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="mode">The transaction middleware mode to use</param>
    public static void UseEntityFrameworkCoreTransactions(this WolverineOptions options, TransactionMiddlewareMode mode)
    {
        try
        {
            options.Services.TryAddSingleton<IDbContextOutboxFactory, DbContextOutboxFactory>();
            registerEFCoreSagaStoreDiagnostics(options.Services);
            options.Services.AddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
            options.Services.AddScoped<IDbContextOutbox, DbContextOutbox>();
            options.Services.AddScoped<OutgoingDomainEvents>();

            // Open-generic registration of Weasel's DbContext cleaner so every
            // DbContext used with Wolverine gets a ready-to-use IDatabaseCleaner<T>
            // without requiring callers to register each one individually. Backs
            // the new host.ResetAllDataAsync<T>() helper and any per-test cleanup
            // in IInitialData-driven dev loops. See GH-2539.
            options.Services.TryAdd(ServiceDescriptor.Singleton(typeof(IDatabaseCleaner<>), typeof(DatabaseCleaner<>)));
            options.Services.TryAdd(ServiceDescriptor.Singleton(typeof(DatabaseCleaner<>), typeof(DatabaseCleaner<>)));

            // CritterWatch (#102): catch any plain `AddDbContext<>` registrations
            // not wired through Wolverine's integration helpers and surface them
            // in the Storage tab with WolverineEnabled = false.
            UntrackedDbContextDiscovery.RegisterImplicitUsageSources(options.Services);
        }
        catch (InvalidOperationException e)
        {
            if (!e.Message.Contains("The service collection cannot be modified because it is read-only."))
            {
                throw;
            }
        }

        options.Include<EntityFrameworkCoreBackedPersistence>();

        // Auto-allow every registered DbContext type for service location.
        // EF Core's AddDbContext<T>(builder) is fundamentally an opaque lambda
        // factory from Wolverine codegen's point of view — there's no way for
        // codegen to inline-construct the DbContext via constructor injection
        // because the configuration is lambda-encapsulated. Without this
        // auto-allow, every handler that takes a DbContext as a parameter
        // would fail under Wolverine 6.0's ServiceLocationPolicy.NotAllowed
        // default, forcing every EF-Core-using application to manually call
        // opts.CodeGeneration.AlwaysUseServiceLocationFor<MyDbContext>() for
        // each context. That's tedious boilerplate that adds no information
        // (the user already opted into EF Core via this very call); auto-
        // allowing keeps the migration friction limited to genuinely opaque
        // non-DbContext registrations.
        autoAllowRegisteredDbContexts(options);

        var providers = options.CodeGeneration.PersistenceProviders();
        var efProvider = providers.OfType<EFCorePersistenceFrameProvider>().FirstOrDefault();
        if (efProvider != null)
        {
            efProvider.DefaultMode = mode;
        }
    }

    /// <summary>
    /// Walks <see cref="WolverineOptions.Services"/> for every registration
    /// whose <see cref="ServiceDescriptor.ServiceType"/> is a concrete subclass
    /// of <see cref="DbContext"/> and adds it to the codegen allow-list via
    /// <see cref="JasperFx.CodeGeneration.GenerationRules.AlwaysUseServiceLocationFor(Type)"/>.
    /// See the call site comment in <see cref="UseEntityFrameworkCoreTransactions(WolverineOptions, TransactionMiddlewareMode)"/>
    /// for the rationale. Idempotent — safe to call multiple times.
    /// </summary>
    private static void autoAllowRegisteredDbContexts(WolverineOptions options)
    {
        foreach (var descriptor in options.Services)
        {
            if (descriptor.ServiceType.IsSubclassOf(typeof(DbContext)))
            {
                options.CodeGeneration.AlwaysUseServiceLocationFor(descriptor.ServiceType);
            }
        }
    }

    /// <summary>
    /// Adds "It just works" support for Wolverine stateful resource support for your EF Core registrations
    /// to build out the table structure implied by your DbContext model at development time.
    ///
    /// This ties in to IServiceCollection.AddResourceSetupAtStartup() and can be enabled or disabled by setting the
    /// JasperFxOptions.Production.ResourceAutoCreate
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCoreWolverineManagedMigrations(this WolverineOptions options)
    {
        options.Services.AddSingleton<ISystemPart, EntityFrameworkCoreSystemPart>();
    }

    internal static bool IsWolverineEnabled(this DbContext dbContext)
    {
        return dbContext.Model.FindAnnotation(WolverineEnabled) != null;
    }

    /// <summary>
    ///     Add entity mappings for Wolverine message storage
    /// </summary>
    /// <param name="model"></param>
    /// <param name="databaseSchema">
    ///     Optionally override the database schema from this DbContext's schema name for just the
    ///     Wolverine mapping tables
    /// </param>
    /// <returns></returns>
    public static ModelBuilder MapWolverineEnvelopeStorage(this ModelBuilder modelBuilder,
        string? databaseSchema = null)
    {
        // SetAnnotation rather than AddAnnotation — the model customizer can be invoked
        // more than once on the same model (e.g., in ancillary-store scenarios where a
        // second DbContext shares the same EF Core model instance), and AddAnnotation
        // throws on a duplicate name. SetAnnotation is idempotent. See #2618.
        modelBuilder.Model.SetAnnotation(WolverineEnabled, "true");

        modelBuilder.Entity<IncomingMessage>(eb =>
        {
            eb.ToTable(DatabaseConstants.IncomingTable, databaseSchema, x => x.ExcludeFromMigrations());

            eb.Property(x => x.Id).HasColumnName(DatabaseConstants.Id);
            eb.HasKey(x => x.Id);


            eb.Property(x => x.Status).HasColumnName(DatabaseConstants.Status).IsRequired();
            eb.Property(x => x.OwnerId).HasColumnName(DatabaseConstants.OwnerId).IsRequired();
            eb.Property(x => x.ExecutionTime).HasColumnName(DatabaseConstants.ExecutionTime).HasDefaultValue(null);
            eb.Property(x => x.Attempts).HasColumnName(DatabaseConstants.Attempts);
            eb.Property(x => x.Body).HasColumnName(DatabaseConstants.Body).IsRequired();
            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
            eb.Property(x => x.ReceivedAt).HasColumnName(DatabaseConstants.ReceivedAt);
            eb.Property(x => x.KeepUntil).HasColumnName(DatabaseConstants.KeepUntil);
        });

        modelBuilder.Entity<OutgoingMessage>(eb =>
        {
            eb.ToTable(DatabaseConstants.OutgoingTable, databaseSchema, x => x.ExcludeFromMigrations());
            eb.Property(x => x.Id).HasColumnName(DatabaseConstants.Id);
            eb.HasKey(x => x.Id);

            eb.Property(x => x.OwnerId).HasColumnName(DatabaseConstants.OwnerId).IsRequired();
            eb.Property(x => x.Destination).HasColumnName(DatabaseConstants.Destination).IsRequired();
            eb.Property(x => x.DeliverBy).HasColumnName(DatabaseConstants.DeliverBy);

            eb.Property(x => x.Body).HasColumnName(DatabaseConstants.Body).IsRequired();
            eb.Property(x => x.Attempts).HasColumnName(DatabaseConstants.Attempts);

            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
        });

        return modelBuilder;
    }

    /// <summary>
    /// Enable Wolverine to "scrape" domain events out of a DomainEvents type collection scoped to the current
    /// Wolverine handler or scoped container
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static WolverineOptions PublishDomainEventsFromEntityFrameworkCore(this WolverineOptions options)
    {
        options.Services.AddScoped<IDomainEventScraper, OutgoingDomainEventsScraper>();
        return options;
    }

    /// <summary>
    /// Tell Wolverine how to "scrape" domain events from the active EF Core DbContext to publish as messages
    /// In this usage, Wolverine is not looking for a specific domain event marker type
    /// </summary>
    /// <param name="options"></param>
    /// <param name="source"></param>
    /// <typeparam name="TEntityType">The base type or common interface type that designates an entity that publishes domain events</typeparam>
    /// <returns></returns>
    public static WolverineOptions PublishDomainEventsFromEntityFrameworkCore<TEntityType>(this WolverineOptions options, 
        Func<TEntityType, IEnumerable<object>> source)
    {
        var scraper = new DomainEventScraper<TEntityType, object>(source);
        options.Services.AddSingleton<IDomainEventScraper>(scraper);
        return options;
    }
    
    /// <summary>
    /// Tell Wolverine how to "scrape" domain events from the active EF Core DbContext to publish as messages
    /// </summary>
    /// <param name="options"></param>
    /// <param name="source"></param>
    /// <typeparam name="TEntityType">The base type or common interface type that designates an entity that publishes domain events</typeparam>
    /// <typeparam name="TDomainEvent">The marker interface for domain events</typeparam>
    /// <returns></returns>
    public static WolverineOptions PublishDomainEventsFromEntityFrameworkCore<TEntityType, TDomainEvent>(this WolverineOptions options, 
        Func<TEntityType, IEnumerable<TDomainEvent>> source)
    {
        var scraper = new DomainEventScraper<TEntityType, TDomainEvent>(source);
        options.Services.AddSingleton<IDomainEventScraper>(scraper);
        return options;
    }
}