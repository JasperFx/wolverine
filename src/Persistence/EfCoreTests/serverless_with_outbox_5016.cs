using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace EfCoreTests;

/// <summary>
/// GH-5016. EF Core under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are persisted in the
/// DbContext's transaction as owned by any node (0), sent inline as soon as that transaction commits with
/// their outbox rows deleted, and anything left behind is recovered by a durability pass.
/// </summary>
public abstract class serverless_with_outbox_5016 : IAsyncLifetime
{
    private readonly string _schemaName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    protected serverless_with_outbox_5016(string schemaName)
    {
        _schemaName = schemaName;
    }

    protected abstract void configureStorage(WolverineOptions opts, string schemaName);

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfCoreServerlessOutboxHandler));

                configureStorage(opts, _schemaName);

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<EfCoreServerlessOutboxCascaded>().To("stub://efcore-serverless-cascaded");

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["efcore-serverless-cascaded"].Sent.ToArray();
    }

    private Task<IReadOnlyList<Envelope>> outboxRowsAsync()
    {
        return theHost.GetRuntime().Storage.Admin.AllOutgoingAsync();
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_when_the_handler_transaction_commits()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new EfCoreServerlessOutboxCommand(id), Token);

        // The handler's entity was committed...
        using (var scope = theHost.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            (await dbContext.Items.FindAsync([id], Token)).ShouldNotBeNull();
        }

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new EfCoreServerlessOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_dbcontext_outbox_sends_inline_when_it_saves_and_flushes()
    {
        var id = Guid.NewGuid();

        using (var scope = theHost.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ItemsDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "Serverless" });
            await outbox.PublishAsync(new EfCoreServerlessOutboxCascaded(id));

            await outbox.SaveChangesAndFlushMessagesAsync(Token);
        }

        sent().Single().Message.ShouldBe(new EfCoreServerlessOutboxCascaded(id));
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unsent_row_from_a_dbcontext_is_owned_by_any_node_and_recovered_by_a_durability_pass()
    {
        var id = Guid.NewGuid();

        // Persisted through the DbContext's outbox, but never sent, as when the request died after the
        // commit and before the inline send
        using (var scope = theHost.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ItemsDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "Unsent" });
            await outbox.PublishAsync(new EfCoreServerlessOutboxCascaded(id));

            await outbox.DbContext.SaveChangesAsync(Token);
        }

        var row = (await outboxRowsAsync()).Single();
        row.OwnerId.ShouldBe(0);

        // Still within OutboxStaleTime, so a pass leaves it to the request that may still be sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        using (var scope = theHost.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            var sql = $"update {_schemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = {{0}} where id = {{1}}";
            await dbContext.Database.ExecuteSqlRawAsync(sql, [DateTimeOffset.UtcNow.Subtract(2.Hours()), row.Id], Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Stores.Single(x => x.StoreUri == theHost.GetRuntime().Storage.Uri)
            .Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sent().Single().Id.ShouldBe(row.Id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

[Collection("postgresql")]
public class serverless_with_outbox_5016_on_postgresql : serverless_with_outbox_5016
{
    public serverless_with_outbox_5016_on_postgresql() : base("serverless_efcore")
    {
    }

    protected override void configureStorage(WolverineOptions opts, string schemaName)
    {
        opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
        {
            o.UseNpgsql(Servers.PostgresConnectionString);
        });

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
    }
}

[Collection("sqlserver")]
public class serverless_with_outbox_5016_on_sqlserver : serverless_with_outbox_5016
{
    public serverless_with_outbox_5016_on_sqlserver() : base("serverless_efcore")
    {
    }

    protected override void configureStorage(WolverineOptions opts, string schemaName)
    {
        opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
        {
            o.UseSqlServer(Servers.SqlServerConnectionString);
        });

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName);
    }
}

public record EfCoreServerlessOutboxCommand(Guid Id);

public record EfCoreServerlessOutboxCascaded(Guid Id);

public static class EfCoreServerlessOutboxHandler
{
    public static EfCoreServerlessOutboxCascaded Handle(EfCoreServerlessOutboxCommand command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "Serverless" });
        return new EfCoreServerlessOutboxCascaded(command.Id);
    }
}
