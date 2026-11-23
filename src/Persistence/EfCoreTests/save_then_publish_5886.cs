using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

/// <summary>
/// GH-5886 on EF Core. A transactional handler that calls SaveChangesAsync() on its own DbContext and then publishes.
/// The durability agent is off, so the only thing that can deliver the message is the pipeline's own flush.
/// </summary>
public class save_then_publish_5886 : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        EfSaveThenPublishCounts.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager)]
    [InlineData(TransactionMiddlewareMode.Lightweight)]
    public async Task a_message_published_after_the_handlers_own_save_changes_is_sent(TransactionMiddlewareMode mode)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ef_save_then_publish");
                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfSaveThenPublishHandlers));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        var message = new EfSaveThenPublish(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        EfSaveThenPublishCounts.Deliveries(message.BeforeId).ShouldBe(1);
        EfSaveThenPublishCounts.Deliveries(message.AfterId).ShouldBe(1);
    }
}

public record EfSaveThenPublish(Guid BeforeId, Guid AfterId);

public record EfSaveThenPublishTarget(Guid Id);

public static class EfSaveThenPublishHandlers
{
    public static async Task Handle(EfSaveThenPublish message, CleanDbContext dbContext, IMessageBus bus)
    {
        await bus.PublishAsync(new EfSaveThenPublishTarget(message.BeforeId));

        dbContext.Items.Add(new Item { Id = message.BeforeId, Name = "saved by the handler" });
        await dbContext.SaveChangesAsync();

        await bus.PublishAsync(new EfSaveThenPublishTarget(message.AfterId));
    }

    public static void Handle(EfSaveThenPublishTarget message) => EfSaveThenPublishCounts.Delivered(message.Id);
}

public static class EfSaveThenPublishCounts
{
    private static readonly ConcurrentDictionary<Guid, int> _deliveries = new();

    public static void Reset() => _deliveries.Clear();

    public static void Delivered(Guid id) => _deliveries.AddOrUpdate(id, 1, (_, n) => n + 1);

    public static int Deliveries(Guid id) => _deliveries.GetValueOrDefault(id);
}
