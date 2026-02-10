using System.Data.Common;
using JasperFx;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public static IServiceCollection AddDbContextWithWolverineIntegration<T>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
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
    public static IServiceCollection AddDbContextWithWolverineIntegration<T>(this IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
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

        if (autoCreate != AutoCreate.None)
        {
            services.AddSingleton<IResourceCreator, TenantedDbContextInitializer<T>>();
        }

        return services;
    }


    private static IServiceCollection addDbContextWithWolverineIntegration<T>(IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
    {
        services.TryAddSingleton<IDbContextOutboxFactory, DbContextOutboxFactory>();
        
        services.AddDbContext<T>((s, b) =>
        {
            configure(s, b);
            b.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

        services.TryAddSingleton<IWolverineExtension, EntityFrameworkCoreBackedPersistence>();
        
        services.TryAddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
        services.TryAddScoped<IDbContextOutbox, DbContextOutbox>();

        return services;
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
            options.Services.AddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
            options.Services.AddScoped<IDbContextOutbox, DbContextOutbox>();
            options.Services.AddScoped<OutgoingDomainEvents>();
        }
        catch (InvalidOperationException e)
        {
            if (!e.Message.Contains("The service collection cannot be modified because it is read-only."))
            {
                throw;
            }
        }

        options.Include<EntityFrameworkCoreBackedPersistence>();

        var providers = options.CodeGeneration.PersistenceProviders();
        var efProvider = providers.OfType<EFCorePersistenceFrameProvider>().FirstOrDefault();
        if (efProvider != null)
        {
            efProvider.DefaultMode = mode;
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
        modelBuilder.Model.AddAnnotation(WolverineEnabled, "true");

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