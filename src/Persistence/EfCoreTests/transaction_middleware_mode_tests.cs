using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

[Collection("sqlserver")]
public class transaction_middleware_mode_tests
{
    [Fact]
    public async Task eager_mode_should_add_transaction_frame()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<EagerModeHandler>();
            }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<EagerModeMessage>()!;

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task lightweight_mode_should_not_add_transaction_frame()
    {
        #region sample_using_lightweight_ef_core_transactions
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                
                // ONLY use SaveChangesAsync() for transaction boundaries
                // Treat the DbContext as a unit of work, assume there are no
                // bulk operations
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LightweightModeHandler>();
            }).StartAsync();

        #endregion

        var chain = host.GetRuntime().Handlers.ChainFor<LightweightModeMessage>()!;

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();
        chain.Middleware.OfType<StartDatabaseTransactionForDbContext>().ShouldBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task transactional_attribute_lightweight_overrides_eager_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LightweightAttributeHandler>()
                    .IncludeType<EagerAutoApplyHandler>();
            }).StartAsync();

        // Verify the auto-applied handler uses the Eager default
        var eagerChain = host.GetRuntime().Handlers.ChainFor<EagerAutoApplyMessage>()!;
        eagerChain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        // Force compilation of the [Transactional] chain by triggering HandlerFor
        host.GetRuntime().Handlers.HandlerFor<LightweightAttributeMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<LightweightAttributeMessage>()!;

        // The attribute overrides to Lightweight, so no transaction frame
        chain.IsTransactional.ShouldBeTrue();
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();
        chain.Middleware.OfType<StartDatabaseTransactionForDbContext>().ShouldBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task transactional_attribute_eager_overrides_lightweight_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<EagerAttributeHandler>()
                    .IncludeType<LightweightAutoApplyHandler>();
            }).StartAsync();

        // Verify the auto-applied handler uses the Lightweight default
        var lightChain = host.GetRuntime().Handlers.ChainFor<LightweightAutoApplyMessage>()!;
        lightChain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();

        // Force compilation of the [Transactional] chain by triggering HandlerFor
        host.GetRuntime().Handlers.HandlerFor<EagerAttributeMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<EagerAttributeMessage>()!;

        // The attribute overrides to Eager, so transaction frame should be present
        chain.IsTransactional.ShouldBeTrue();
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task lightweight_attribute_with_storage_side_effects_should_not_add_transaction_frame()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LightweightStorageSideEffectHandler>();
            }).StartAsync();

        // Force compilation
        host.GetRuntime().Handlers.HandlerFor<LightweightStorageSideEffectMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<LightweightStorageSideEffectMessage>()!;

        // The [Transactional(Mode = Lightweight)] should override even with Storage side effects
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();
        chain.Middleware.OfType<StartDatabaseTransactionForDbContext>().ShouldBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task eager_attribute_with_storage_side_effects_should_add_transaction_frame()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<EagerStorageSideEffectHandler>();
            }).StartAsync();

        // Force compilation
        host.GetRuntime().Handlers.HandlerFor<EagerStorageSideEffectMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<EagerStorageSideEffectMessage>()!;

        // The [Transactional(Mode = Eager)] should override the Lightweight default
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task default_mode_is_eager()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<DefaultModeHandler>();
            }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<DefaultModeMessage>()!;

        // Default should be Eager
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task handler_policy_eager_mode_is_honored_for_storage_action_saga_chains()
    {
        // GH-3039: global Lightweight, but a marker-interface IHandlerPolicy opts specific messages into
        // Eager by setting chain.Tags["TransactionMiddlewareMode"]. A saga handler returning a storage
        // action (Insert<T>) must honor that policy-set mode - previously it was silently ignored because
        // SideEffectPolicy applied the (Lightweight) transaction support before the user policy ran.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<EagerPolicySagaDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.Policies.AutoApplyTransactions();

                // Opt the marker-interface messages into Eager from a policy, the natural per-message way.
                opts.Policies.Add<EagerTransactionForMessagePolicy<IRequiresEagerTransaction>>();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<EagerPolicySaga>();
            }).StartAsync();

        host.GetRuntime().Handlers.HandlerFor<StartEagerPolicySaga>();
        var chain = host.GetRuntime().Handlers.ChainFor<StartEagerPolicySaga>()!;

        // The policy set Eager, so the storage-action saga chain must get the eager transaction frames.
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task multiple_dbContexts_without_main_dbContext_should_throw_exception()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<ConflictAppDbContext>(x => x.UseInMemoryDatabase("app"));
                opts.Services.AddDbContextWithWolverineIntegration<ConflictCommunDbContext>(x => x.UseInMemoryDatabase("commun"));

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MultipleDbContextsHandler>();
            });

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await builder.StartAsync();
        });

        exception.Message.ShouldContain("Cannot determine the DbContext type");
        exception.Message.ShouldContain("multiple DbContext types detected: ConflictAppDbContext, ConflictCommunDbContext");
    }

    [Fact]
    public async Task multiple_dbContexts_with_main_dbContext_via_fluent_api_should_resolve_conflict()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<ConflictAppDbContext>(x => x.UseInMemoryDatabase("app"));
                opts.Services.AddDbContextWithWolverineIntegration<ConflictCommunDbContext>(x => x.UseInMemoryDatabase("commun"));

                opts.UseEntityFrameworkCoreTransactions()
                    .UseMainDbContext<ConflictAppDbContext>();
                
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MultipleDbContextsHandler>();
            }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<MultipleDbContextsMessage>()!;
        
        chain.Middleware.OfType<EnrollDbContextInTransaction>()
            .Any(x => x.DbContextType == typeof(ConflictAppDbContext))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task multiple_dbContexts_with_main_dbContext_via_options_should_resolve_conflict()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<ConflictAppDbContext>(x => x.UseInMemoryDatabase("app"));
                opts.Services.AddDbContextWithWolverineIntegration<ConflictCommunDbContext>(x => x.UseInMemoryDatabase("commun"));

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseMainDbContext<ConflictAppDbContext>();
                
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MultipleDbContextsHandler>();
            }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<MultipleDbContextsMessage>()!;
        
        chain.Middleware.OfType<EnrollDbContextInTransaction>()
            .Any(x => x.DbContextType == typeof(ConflictAppDbContext))
            .ShouldBeTrue();
    }
}

public interface IRequiresEagerTransaction;

// Mirrors the marker-interface policy from GH-3039: opt every message of a given type into eager
// transactions from a single IHandlerPolicy rather than per-method [Transactional] attributes.
public class EagerTransactionForMessagePolicy<TMessage> : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(c => typeof(TMessage).IsAssignableFrom(c.MessageType)))
        {
            chain.Tags["TransactionMiddlewareMode"] = TransactionMiddlewareMode.Eager;
        }
    }
}

public record StartEagerPolicySaga(Guid Id) : IRequiresEagerTransaction;

public class EagerPolicySaga : Saga
{
    public Guid Id { get; set; }

    // Saga start whose return value is a storage action for another entity - the GH-3039 shape.
    public Insert<Item> Start(StartEagerPolicySaga message)
    {
        return Storage.Insert(new Item { Id = Guid.NewGuid(), Name = "from-saga" });
    }
}

public class EagerPolicySagaDbContext : DbContext
{
    public EagerPolicySagaDbContext(DbContextOptions<EagerPolicySagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("policy_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });

        modelBuilder.Entity<EagerPolicySaga>(map =>
        {
            map.ToTable("policy_saga");
            map.HasKey(x => x.Id);
        });
    }
}

#region test message types and handlers

public record EagerModeMessage;

public class EagerModeHandler
{
    public static void Handle(EagerModeMessage message, CleanDbContext db)
    {
    }
}

public record LightweightModeMessage;

public class LightweightModeHandler
{
    public static void Handle(LightweightModeMessage message, CleanDbContext db)
    {
    }
}

public record LightweightAttributeMessage;

#region sample_explicit_usage_of_transaction_middleware_mode
public class LightweightAttributeHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
    public static void Handle(LightweightAttributeMessage message, CleanDbContext db)
    {
    }
}

#endregion

public record EagerAttributeMessage;

public class EagerAttributeHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Eager)]
    public static void Handle(EagerAttributeMessage message, CleanDbContext db)
    {
    }
}

public record DefaultModeMessage;

public class DefaultModeHandler
{
    public static void Handle(DefaultModeMessage message, CleanDbContext db)
    {
    }
}

public record EagerAutoApplyMessage;

public class EagerAutoApplyHandler
{
    public static void Handle(EagerAutoApplyMessage message, CleanDbContext db)
    {
    }
}

public record LightweightAutoApplyMessage;

public class LightweightAutoApplyHandler
{
    public static void Handle(LightweightAutoApplyMessage message, CleanDbContext db)
    {
    }
}

public record LightweightStorageSideEffectMessage;

public class LightweightStorageSideEffectHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
    public static Insert<Item> Handle(LightweightStorageSideEffectMessage message)
    {
        return Storage.Insert(new Item { Id = Guid.NewGuid(), Name = "test" });
    }
}

public record EagerStorageSideEffectMessage;

public class EagerStorageSideEffectHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Eager)]
    public static Insert<Item> Handle(EagerStorageSideEffectMessage message)
    {
        return Storage.Insert(new Item { Id = Guid.NewGuid(), Name = "test" });
    }
}

public class ConflictAppDbContext : DbContext
{
    public ConflictAppDbContext(DbContextOptions<ConflictAppDbContext> options) : base(options) {}
}

public class ConflictCommunDbContext : DbContext
{
    public ConflictCommunDbContext(DbContextOptions<ConflictCommunDbContext> options) : base(options) {}
}

public record MultipleDbContextsMessage;

public class MultipleDbContextsHandler
{
    public static void Handle(MultipleDbContextsMessage message, ConflictAppDbContext db1, ConflictCommunDbContext db2)
    {
    }
}

#endregion
