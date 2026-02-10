using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;
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

        var chain = host.GetRuntime().Handlers.ChainFor<EagerModeMessage>();

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task lightweight_mode_should_not_add_transaction_frame()
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
                    .IncludeType<LightweightModeHandler>();
            }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<LightweightModeMessage>();

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
        var eagerChain = host.GetRuntime().Handlers.ChainFor<EagerAutoApplyMessage>();
        eagerChain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();

        // Force compilation of the [Transactional] chain by triggering HandlerFor
        host.GetRuntime().Handlers.HandlerFor<LightweightAttributeMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<LightweightAttributeMessage>();

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
        var lightChain = host.GetRuntime().Handlers.ChainFor<LightweightAutoApplyMessage>();
        lightChain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();

        // Force compilation of the [Transactional] chain by triggering HandlerFor
        host.GetRuntime().Handlers.HandlerFor<EagerAttributeMessage>();
        var chain = host.GetRuntime().Handlers.ChainFor<EagerAttributeMessage>();

        // The attribute overrides to Eager, so transaction frame should be present
        chain.IsTransactional.ShouldBeTrue();
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

        var chain = host.GetRuntime().Handlers.ChainFor<DefaultModeMessage>();

        // Default should be Eager
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
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

public class LightweightAttributeHandler
{
    [Transactional(Mode = TransactionMiddlewareMode.Lightweight)]
    public static void Handle(LightweightAttributeMessage message, CleanDbContext db)
    {
    }
}

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

#endregion
