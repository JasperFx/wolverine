using System;
using Lamar;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

public static class WolverineEntityCoreExtensions
{
    internal const string WolverineEnabled = "WolverineEnabled";

    public static IServiceCollection AddDbContextWithWolverineIntegration<T>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure) where T : DbContext
    {
        services.AddDbContext<T>(b =>
        {
            configure(b);
            b.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

        return services;
    }
    
    /// <summary>
    ///     Uses Entity Framework Core for Saga persistence and transactional
    ///     middleware
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCoreTransactions(this WolverineOptions options)
    {
        options.Include<EntityFrameworkCoreBackedPersistence>();
    }

    internal static bool IsWolverineEnabled(this DbContext dbContext)
    {
        return dbContext.Model.FindAnnotation(WolverineEntityCoreExtensions.WolverineEnabled) != null;
    }

    internal static IEnvelopeTransaction BuildTransaction(this DbContext dbContext, MessageContext context)
    {
        return dbContext.IsWolverineEnabled() 
            ? new MappedEnvelopeTransaction(dbContext, context) 
            : new RawDatabaseEnvelopeTransaction(dbContext, context);
    }
    
    /// <summary>
    /// Add entity mappings for Wolverine message storage
    /// </summary>
    /// <param name="model"></param>
    /// <param name="databaseSchema">Optionally override the database schema from this DbContext's schema name for just the Wolverine mapping tables</param>
    /// <returns></returns>
    public static ModelBuilder MapWolverineEnvelopeStorage(this ModelBuilder modelBuilder, string? databaseSchema = null)
    {
        modelBuilder.Model.AddAnnotation(WolverineEnabled, "true");
        
        modelBuilder.Entity<IncomingMessage>(eb =>
        {
            var table = eb.ToTable(DatabaseConstants.IncomingTable, databaseSchema, x => x.ExcludeFromMigrations());


            eb.Property(x => x.Id).HasColumnName(DatabaseConstants.Id);
            eb.HasKey(x => x.Id);

                
            eb.Property(x => x.Status).HasColumnName(DatabaseConstants.Status).IsRequired();
            eb.Property(x => x.OwnerId).HasColumnName(DatabaseConstants.OwnerId).IsRequired();
            eb.Property(x => x.ExecutionTime).HasColumnName(DatabaseConstants.ExecutionTime).HasDefaultValue(null);
            eb.Property(x => x.Attempts).HasColumnName(DatabaseConstants.Attempts).HasDefaultValue(0);
            eb.Property(x => x.Body).HasColumnName(DatabaseConstants.Body).IsRequired();
            eb.Property(x => x.ConversationId).HasColumnName(DatabaseConstants.ConversationId);
            eb.Property(x => x.CorrelationId).HasColumnName(DatabaseConstants.CorrelationId);
            eb.Property(x => x.ParentId).HasColumnName(DatabaseConstants.ParentId);
            eb.Property(x => x.SagaId).HasColumnName(DatabaseConstants.SagaId);
            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
            eb.Property(x => x.ContentType).HasColumnName(DatabaseConstants.ContentType);
            eb.Property(x => x.ReplyRequested).HasColumnName(DatabaseConstants.ReplyRequested);
            eb.Property(x => x.AckRequested).HasColumnName(DatabaseConstants.AckRequested);
            eb.Property(x => x.ReplyUri).HasColumnName(DatabaseConstants.ReplyUri);
            eb.Property(x => x.ReceivedAt).HasColumnName(DatabaseConstants.ReceivedAt);
            eb.Property(x => x.SentAt).HasColumnName(DatabaseConstants.SentAt);
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
            
            eb.Property(x => x.ConversationId).HasColumnName(DatabaseConstants.ConversationId);
            eb.Property(x => x.CorrelationId).HasColumnName(DatabaseConstants.CorrelationId);
            eb.Property(x => x.ParentId).HasColumnName(DatabaseConstants.ParentId);
            eb.Property(x => x.SagaId).HasColumnName(DatabaseConstants.SagaId);
            eb.Property(x => x.MessageType).HasColumnName(DatabaseConstants.MessageType).IsRequired();
            eb.Property(x => x.ContentType).HasColumnName(DatabaseConstants.ContentType);
            eb.Property(x => x.ReplyRequested).HasColumnName(DatabaseConstants.ReplyRequested);
            eb.Property(x => x.AckRequested).HasColumnName(DatabaseConstants.AckRequested);
            eb.Property(x => x.ReplyUri).HasColumnName(DatabaseConstants.ReplyUri);
            eb.Property(x => x.SentAt).HasColumnName(DatabaseConstants.SentAt);
        });

        return modelBuilder;
    }
}