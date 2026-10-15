using System.Collections.Concurrent;
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
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace EfCoreTests;

/// <summary>
/// Exploratory tests for PR #5044 (DurabilityMode.ServerlessWithOutbox) with EF Core: TransactionMiddlewareMode
/// Eager and Lightweight with cascades, with and without the Wolverine envelope mapping in the DbContext, and
/// IDbContextOutbox used directly outside of any handler (as from a plain minimal API endpoint, which resolves it
/// from the request scope exactly like this). Sends go over TCP so a real DurableSendingAgent is in play.
/// </summary>
public abstract class serverless_with_outbox_exit_paths_exploratory_5044 : IAsyncDisposable
{
    private readonly string _schemaName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
    private IHost? theFunction;
    private IHost? theReceiver;

    protected serverless_with_outbox_exit_paths_exploratory_5044(string schemaName)
    {
        _schemaName = schemaName;
    }

    protected abstract void configureStorage(WolverineOptions opts, string schemaName, bool wolverineMapping);

    public async ValueTask DisposeAsync()
    {
        if (theFunction != null)
        {
            await theFunction.StopAsync();
            theFunction.Dispose();
        }

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    private async Task startTheFunctionAsync(TransactionMiddlewareMode mode, bool wolverineMapping)
    {
        theFunction = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 100.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044EfHandler));

                configureStorage(opts, _schemaName, wolverineMapping);

                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<Explore5044EfCascaded>().ToPort(thePort);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(Token);

        await theFunction.ResetResourceState();
    }

    private async Task startTheReceiverAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044EfReceiver));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    private Task<IReadOnlyList<Envelope>> outboxRowsAsync()
        => theFunction!.GetRuntime().Storage.Admin.AllOutgoingAsync();

    private static async Task shouldBeReceivedAsync(Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (!Explore5044EfReceiver.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout) throw new TimeoutException($"Message {id} never reached the receiver");
            await Task.Delay(50, Token);
        }
    }

    private async Task assertDeliveredAsync(Guid id, bool receiverIsUp)
    {
        var rows = await outboxRowsAsync();

        if (receiverIsUp)
        {
            // Sent inline before the call returned, nothing left behind
            rows.ShouldBeEmpty();
            await shouldBeReceivedAsync(id);
            return;
        }

        rows.Count.ShouldBe(1);
        rows.Single().OwnerId.ShouldBe(TransportConstants.AnyNode);

        await startTheReceiverAsync();
        await Task.Delay(200, Token);
        await theFunction!.RunDurabilityPassAsync(token: Token);

        await shouldBeReceivedAsync(id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, true, true)]
    [InlineData(TransactionMiddlewareMode.Eager, true, false)]
    [InlineData(TransactionMiddlewareMode.Eager, false, true)]
    [InlineData(TransactionMiddlewareMode.Eager, false, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, true, true)]
    [InlineData(TransactionMiddlewareMode.Lightweight, true, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, false, true)]
    [InlineData(TransactionMiddlewareMode.Lightweight, false, false)]
    public async Task handler_cascade(TransactionMiddlewareMode mode, bool wolverineMapping, bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(mode, wolverineMapping);

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044EfCommand(id), Token);

        await assertDeliveredAsync(id, receiverIsUp);
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, true)]
    [InlineData(TransactionMiddlewareMode.Eager, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, true)]
    [InlineData(TransactionMiddlewareMode.Lightweight, false)]
    public async Task handler_publish_through_imessagecontext(TransactionMiddlewareMode mode, bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(mode, true);

        var id = Guid.NewGuid();
        await theFunction!.MessageBus().InvokeAsync(new Explore5044EfPublishCommand(id), Token);

        await assertDeliveredAsync(id, receiverIsUp);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task dbcontext_outbox_outside_a_handler(bool wolverineMapping, bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(TransactionMiddlewareMode.Eager, wolverineMapping);

        var id = Guid.NewGuid();

        // What a minimal API endpoint taking IDbContextOutbox<ItemsDbContext> gets from its request scope
        using (var scope = theFunction!.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ItemsDbContext>>();
            outbox.DbContext.Items.Add(new Item { Id = id, Name = "explore5044" });
            await outbox.PublishAsync(new Explore5044EfCascaded(id));
            await outbox.SaveChangesAndFlushMessagesAsync(Token);
        }

        await assertDeliveredAsync(id, receiverIsUp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task generic_dbcontext_outbox_enrolled_then_flushed(bool receiverIsUp)
    {
        if (receiverIsUp) await startTheReceiverAsync();
        await startTheFunctionAsync(TransactionMiddlewareMode.Eager, true);

        var id = Guid.NewGuid();

        using (var scope = theFunction!.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            outbox.Enroll(dbContext);

            dbContext.Items.Add(new Item { Id = id, Name = "explore5044" });
            await outbox.PublishAsync(new Explore5044EfCascaded(id));
            await outbox.SaveChangesAndFlushMessagesAsync(Token);
        }

        await assertDeliveredAsync(id, receiverIsUp);
    }
}

[Collection("postgresql")]
public class serverless_with_outbox_exit_paths_exploratory_5044_on_postgresql
    : serverless_with_outbox_exit_paths_exploratory_5044
{
    public serverless_with_outbox_exit_paths_exploratory_5044_on_postgresql() : base("explore5044_exit_ef")
    {
    }

    protected override void configureStorage(WolverineOptions opts, string schemaName, bool wolverineMapping)
    {
        if (wolverineMapping)
        {
            opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                o.UseNpgsql(Servers.PostgresConnectionString));
        }
        else
        {
            opts.Services.AddDbContext<ItemsDbContext>(o => o.UseNpgsql(Servers.PostgresConnectionString));
        }

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
    }
}

[Collection("sqlserver")]
public class serverless_with_outbox_exit_paths_exploratory_5044_on_sqlserver
    : serverless_with_outbox_exit_paths_exploratory_5044
{
    public serverless_with_outbox_exit_paths_exploratory_5044_on_sqlserver() : base("explore5044_exit_ef")
    {
    }

    protected override void configureStorage(WolverineOptions opts, string schemaName, bool wolverineMapping)
    {
        if (wolverineMapping)
        {
            opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                o.UseSqlServer(Servers.SqlServerConnectionString));
        }
        else
        {
            opts.Services.AddDbContext<ItemsDbContext>(o => o.UseSqlServer(Servers.SqlServerConnectionString));
        }

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName);
    }
}

public record Explore5044EfCommand(Guid Id);

public record Explore5044EfPublishCommand(Guid Id);

public record Explore5044EfCascaded(Guid Id);

public static class Explore5044EfHandler
{
    public static Explore5044EfCascaded Handle(Explore5044EfCommand command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "explore5044" });
        return new Explore5044EfCascaded(command.Id);
    }

    public static async Task Handle(Explore5044EfPublishCommand command, ItemsDbContext dbContext,
        IMessageContext context)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "explore5044" });
        await context.PublishAsync(new Explore5044EfCascaded(command.Id));
    }
}

public static class Explore5044EfReceiver
{
    public static readonly ConcurrentDictionary<Guid, bool> Received = new();

    public static void Handle(Explore5044EfCascaded message) => Received[message.Id] = true;
}
