using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Durability;
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
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static IServiceCollection AddDbContextWithWolverineManagedMultiTenancy<T>(this IServiceCollection services,
        Action<DbContextOptionsBuilder<T>, string> dbContextConfiguration) where T : DbContext
    {
        // For code generation
        services.TryAddSingleton<IWolverineExtension, EntityFrameworkCoreBackedPersistence>();
        
        services.AddSingleton<IDbContextBuilder<T>>(s =>
        {
            var store = s.GetRequiredService<IMessageStore>();
            var tenanted = store as MultiTenantedMessageStore;
            if (tenanted == null || tenanted.Main is not IMessageDatabase)
            {
                throw new InvalidOperationException(
                    $"Configured multi-tenanted usage of {typeof(T).FullNameInCode()} requires multi-tenanted Wolverine database storage");
            }

            return new TenantedDbContextBuilder<T>(s, tenanted, dbContextConfiguration);
        });
        
        // TODO -- need a multi-tenanted version of this
        // services.TryAddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
        // services.TryAddScoped<IDbContextOutbox, DbContextOutbox>();

        return services;
    }

    private static IServiceCollection addDbContextWithWolverineIntegration<T>(IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure, string? wolverineDatabaseSchema = null) where T : DbContext
    {
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
    ///     middleware
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCoreTransactions(this WolverineOptions options)
    {
        try
        {
            options.Services.AddScoped(typeof(IDbContextOutbox<>), typeof(DbContextOutbox<>));
            options.Services.AddScoped<IDbContextOutbox, DbContextOutbox>();
        }
        catch (InvalidOperationException e)
        {
            if (!e.Message.Contains("The service collection cannot be modified because it is read-only."))
            {
                throw;
            }
        }
        
        options.Include<EntityFrameworkCoreBackedPersistence>();
    }

    internal static bool IsWolverineEnabled(this DbContext dbContext)
    {
        return dbContext.Model.FindAnnotation(WolverineEnabled) != null;
    }

    public static IEnvelopeTransaction BuildTransaction(this DbContext dbContext, MessageContext context)
    {
        return dbContext.IsWolverineEnabled()
            ? new MappedEnvelopeTransaction(dbContext, context)
            : new RawDatabaseEnvelopeTransaction(dbContext, context);
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
            eb.Property(x => x.Attempts).HasColumnName(DatabaseConstants.Attempts).HasDefaultValue(0);
            eb.Property(x => x.Body).HasColumnName(DatabaseConstants.Body).IsRequired();
            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
            eb.Property(x => x.ReceivedAt).HasColumnName(DatabaseConstants.ReceivedAt);
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
            eb.Property(x => x.Attempts).HasColumnName(DatabaseConstants.Attempts).HasDefaultValue(0);

            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
        });

        return modelBuilder;
    }
}