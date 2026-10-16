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
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace EfCoreTests;

/// <summary>
/// GH-5046. EF Core under DurabilityMode.ServerlessWithOutbox: a message this application handles itself is written
/// to the inbox in the DbContext's transaction, owned by any node (0), and the durability pass handles it. A scheduled
/// one is released by the pass when it is due.
/// </summary>
public abstract class serverless_with_outbox_local_messages_5046 : IAsyncLifetime
{
    private readonly string _schemaName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    protected serverless_with_outbox_local_messages_5046(string schemaName)
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
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfCoreGh5046Handlers));

                configureStorage(opts, _schemaName);

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Envelope[]> waitingInboxRowsAsync()
    {
        var rows = await theHost.GetRuntime().Storage.Admin.AllIncomingAsync();
        return rows.Where(x => x.Status != EnvelopeStatus.Handled).ToArray();
    }

    private async Task<Envelope> singleRowWaitingForAPassAsync(EnvelopeStatus status)
    {
        var row = (await waitingInboxRowsAsync()).Single();
        row.Status.ShouldBe(status);
        row.OwnerId.ShouldBe(TransportConstants.AnyNode);
        row.MessageType.ShouldBe(typeof(EfCoreGh5046Local).ToMessageTypeName());
        return row;
    }

    [Fact]
    public async Task a_local_cascade_is_written_in_the_dbcontext_transaction_and_handled_by_a_pass()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new EfCoreGh5046Command(id), Token);

        using (var scope = theHost.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            (await dbContext.Items.FindAsync([id], Token)).ShouldNotBeNull();
        }

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Incoming);
        EfCoreGh5046Handlers.Handled.ContainsKey(id).ShouldBeFalse();

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.IncomingRecovered.ShouldBe(1);
        EfCoreGh5046Handlers.Handled[id].ShouldBe(1);
        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_local_cascade_is_rolled_back_with_the_dbcontext_transaction()
    {
        var id = Guid.NewGuid();

        await Should.ThrowAsync<DivideByZeroException>(async () =>
            await theHost.MessageBus().InvokeAsync(new EfCoreGh5046CommandThenFail(id), Token));

        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_scheduled_local_cascade_is_released_by_a_pass_when_due()
    {
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeAsync(new EfCoreGh5046ScheduleCommand(id), Token);

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Scheduled);

        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.ScheduledReleased.ShouldBe(0);
        EfCoreGh5046Handlers.Handled.ContainsKey(id).ShouldBeFalse();

        await Task.Delay(1500, Token);

        var due = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        due.ScheduledReleased.ShouldBe(1);
        EfCoreGh5046Handlers.Handled[id].ShouldBe(1);
        (await waitingInboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_dbcontext_outbox_writes_a_locally_handled_message_for_a_pass()
    {
        var id = Guid.NewGuid();

        using (var scope = theHost.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ItemsDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "GH-5046" });
            await outbox.PublishAsync(new EfCoreGh5046Local(id));

            await outbox.SaveChangesAndFlushMessagesAsync(Token);
        }

        await singleRowWaitingForAPassAsync(EnvelopeStatus.Incoming);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.IncomingRecovered.ShouldBe(1);
        EfCoreGh5046Handlers.Handled[id].ShouldBe(1);
    }
}

[Collection("postgresql")]
public class serverless_with_outbox_local_messages_5046_on_postgresql : serverless_with_outbox_local_messages_5046
{
    public serverless_with_outbox_local_messages_5046_on_postgresql() : base("gh5046_efcore")
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
public class serverless_with_outbox_local_messages_5046_on_sqlserver : serverless_with_outbox_local_messages_5046
{
    public serverless_with_outbox_local_messages_5046_on_sqlserver() : base("gh5046_efcore")
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

public record EfCoreGh5046Command(Guid Id);

public record EfCoreGh5046CommandThenFail(Guid Id);

public record EfCoreGh5046ScheduleCommand(Guid Id);

public record EfCoreGh5046Local(Guid Id);

public static class EfCoreGh5046Handlers
{
    public static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public static EfCoreGh5046Local Handle(EfCoreGh5046Command command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "GH-5046" });
        return new EfCoreGh5046Local(command.Id);
    }

    public static async Task Handle(EfCoreGh5046CommandThenFail command, ItemsDbContext dbContext,
        IMessageContext context)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "GH-5046" });
        await context.PublishAsync(new EfCoreGh5046Local(command.Id));
        throw new DivideByZeroException();
    }

    public static object Handle(EfCoreGh5046ScheduleCommand command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = command.Id, Name = "GH-5046" });
        return new EfCoreGh5046Local(command.Id).DelayedFor(1.Seconds());
    }

    public static void Handle(EfCoreGh5046Local message) => Handled.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}
