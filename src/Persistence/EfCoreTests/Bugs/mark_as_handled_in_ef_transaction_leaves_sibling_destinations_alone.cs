using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// One message type is delivered to two Rabbit MQ queues, so under MessageIdentity.IdAndDestination the inbox holds one
// row per queue with the SAME envelope id. The handler on the first queue runs inside an EF Core transaction, and
// EfCoreEnvelopeTransaction.CommitAsync marks its inbox row handled on the application's own connection. That statement
// used to match the id alone, so it also retired the second queue's row while the second handler had not run yet. A node
// that stopped at that point left the second copy Handled; the durability agent only recovers Incoming rows, so the copy
// was lost without a trace -- no dead letter, no log.
//
// The two gates force the only interleaving that exposes it: the EF handler may not commit until the gated handler has
// started, which guarantees the second row is already persisted (the durable receiver stores before it executes).

public record MessageForTwoDestinations(Guid Id);

public sealed class TwoDestinationsGates
{
    public TaskCompletionSource GatedHandlerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseGatedHandler { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[StickyHandler(mark_as_handled_in_ef_transaction_leaves_sibling_destinations_alone.EfQueue)]
public sealed class MessageForTwoDestinationsEfHandler
{
    public async Task Handle(MessageForTwoDestinations message, TwoDestinationsDbContext dbContext,
        TwoDestinationsGates gates)
    {
        dbContext.Items.Add(new TwoDestinationsItem { Id = message.Id });

        // Do not let the transaction commit until the sibling delivery is in the inbox and being handled
        await gates.GatedHandlerStarted.Task.WaitAsync(30.Seconds());
    }
}

[StickyHandler(mark_as_handled_in_ef_transaction_leaves_sibling_destinations_alone.GatedQueue)]
public sealed class MessageForTwoDestinationsGatedHandler
{
    public async Task Handle(MessageForTwoDestinations message, TwoDestinationsGates gates)
    {
        gates.GatedHandlerStarted.TrySetResult();
        await gates.ReleaseGatedHandler.Task.WaitAsync(30.Seconds());
    }
}

public sealed class TwoDestinationsItem
{
    public Guid Id { get; set; }
}

public sealed class TwoDestinationsDbContext : DbContext
{
    public TwoDestinationsDbContext(DbContextOptions<TwoDestinationsDbContext> options) : base(options)
    {
    }

    public DbSet<TwoDestinationsItem> Items => Set<TwoDestinationsItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("two_destinations");
        modelBuilder.Entity<TwoDestinationsItem>().ToTable("items");
    }
}

public class mark_as_handled_in_ef_transaction_leaves_sibling_destinations_alone : IAsyncLifetime
{
    public const string EfQueue = "two-destinations-ef";
    public const string GatedQueue = "two-destinations-gated";
    private const string Exchange = "two-destinations";

    private readonly TwoDestinationsGates _gates = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Other handlers in this assembly need persistence this host does not register
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MessageForTwoDestinationsEfHandler>()
                    .IncludeType<MessageForTwoDestinationsGatedHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // One logical message, two physical deliveries: the destination has to be part of the inbox identity
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<MessageForTwoDestinations>()
                    .ToRabbitExchange(Exchange, e =>
                    {
                        e.BindQueue(EfQueue);
                        e.BindQueue(GatedQueue);
                    })
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(EfQueue).Named(EfQueue).UseDurableInbox();
                opts.ListenToRabbitQueue(GatedQueue).Named(GatedQueue).UseDurableInbox();

                opts.Services.AddSingleton(_gates);

                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<TwoDestinationsDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "two_destinations_wolverine");

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        // Never leave a handler parked, or shutdown waits on it
        _gates.GatedHandlerStarted.TrySetResult();
        _gates.ReleaseGatedHandler.TrySetResult();

        await _host.StopAsync();
        _host.Dispose();
        SqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task committing_the_ef_handler_leaves_the_sibling_destination_incoming()
    {
        var efDestination = new Uri($"rabbitmq://queue/{EfQueue}");
        var gatedDestination = new Uri($"rabbitmq://queue/{GatedQueue}");

        await _host.MessageBus().PublishAsync(new MessageForTwoDestinations(Guid.NewGuid()));

        // The EF handler's commit is what marks its own row Handled -- the moment the sibling used to be retired too
        var rows = await waitForRowsAsync(rows =>
            rows.Any(x => x.Destination == efDestination && x.Status == EnvelopeStatus.Handled));

        rows.Length.ShouldBe(2, "One inbox row per destination under IdAndDestination");
        rows.Select(x => x.Id).Distinct().Count().ShouldBe(1, "Both deliveries carry the same envelope id");

        rows.Single(x => x.Destination == gatedDestination).Status.ShouldBe(EnvelopeStatus.Incoming,
            "The gated handler has not finished, so its row must stay recoverable. Marked Handled here, a node that stops now loses this copy for good.");

        _gates.ReleaseGatedHandler.TrySetResult();

        await waitForRowsAsync(rows =>
            rows.Any(x => x.Destination == gatedDestination && x.Status == EnvelopeStatus.Handled));
    }

    private async Task<Envelope[]> waitForRowsAsync(Func<Envelope[], bool> condition)
    {
        var messageTypeName = typeof(MessageForTwoDestinations).ToMessageTypeName();
        var storage = _host.GetRuntime().Storage;
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());

        while (true)
        {
            var rows = (await storage.Admin.AllIncomingAsync())
                .Where(x => x.MessageType == messageTypeName)
                .ToArray();

            if (condition(rows)) return rows;

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The inbox never reached the expected state. Rows: {rows.Select(x => $"{x.Destination} {x.Status}").Join(", ")}");
            }

            await Task.Delay(100.Milliseconds());
        }
    }
}
