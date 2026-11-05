using System.Data;
using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

// GH-5207: the isolation level of the explicit transaction Eager mode opens
[Collection("sqlserver")]
public class transaction_isolation_level_5207
{
    private static async Task<IHost> startAsync(Action<EFCoreTransactionConfiguration>? configure,
        TransactionMiddlewareMode mode = TransactionMiddlewareMode.Eager)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txisolation");

                var config = opts.UseEntityFrameworkCoreTransactions(mode);
                configure?.Invoke(config);

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<IsolationDefaultHandler>()
                    .IncludeType<IsolationOverrideHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static string sourceFor<T>(IHost host)
    {
        var handlers = host.GetRuntime().Handlers;
        handlers.HandlerFor<T>();
        return handlers.ChainFor<T>()!.SourceCode!;
    }

    [Fact]
    public async Task no_configuration_keeps_the_provider_default()
    {
        using var host = await startAsync(null);
        var code = sourceFor<IsolationDefaultMessage>(host);

        code.ShouldContain("BeginTransactionAsync(");
        code.ShouldNotContain("IsolationLevel");
    }

    [Fact]
    public async Task global_default_is_applied()
    {
        using var host = await startAsync(x => x.WithIsolationLevel(IsolationLevel.RepeatableRead));
        sourceFor<IsolationDefaultMessage>(host)
            .ShouldContain("System.Data.IsolationLevel.RepeatableRead");
    }

    [Fact]
    public async Task attribute_overrides_the_global_default()
    {
        using var host = await startAsync(x => x.WithIsolationLevel(IsolationLevel.RepeatableRead));
        var code = sourceFor<IsolationOverrideMessage>(host);
        code.ShouldContain("System.Data.IsolationLevel.Serializable");
        code.ShouldNotContain("RepeatableRead");
    }

    [Fact]
    public async Task the_handler_runs_inside_a_transaction_at_the_configured_level()
    {
        using var host = await startAsync(x => x.WithIsolationLevel(IsolationLevel.RepeatableRead));

        IsolationDefaultHandler.Observed = null;
        IsolationOverrideHandler.Observed = null;

        await host.InvokeMessageAndWaitAsync(new IsolationDefaultMessage());
        await host.InvokeMessageAndWaitAsync(new IsolationOverrideMessage());

        IsolationDefaultHandler.Observed.ShouldBe(IsolationLevel.RepeatableRead);
        IsolationOverrideHandler.Observed.ShouldBe(IsolationLevel.Serializable);
    }

    [Fact]
    public async Task lightweight_mode_opens_no_explicit_transaction()
    {
        using var host = await startAsync(x => x.WithIsolationLevel(IsolationLevel.RepeatableRead),
            TransactionMiddlewareMode.Lightweight);
        sourceFor<IsolationDefaultMessage>(host).ShouldNotContain("IsolationLevel");
    }
}

public record IsolationDefaultMessage;

public class IsolationDefaultHandler
{
    public static IsolationLevel? Observed;

    public static void Handle(IsolationDefaultMessage message, CleanDbContext db)
    {
        Observed = db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
    }
}

public record IsolationOverrideMessage;

public class IsolationOverrideHandler
{
    public static IsolationLevel? Observed;

    [Transactional(IsolationLevel = IsolationLevel.Serializable)]
    public static void Handle(IsolationOverrideMessage message, CleanDbContext db)
    {
        Observed = db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
    }
}
