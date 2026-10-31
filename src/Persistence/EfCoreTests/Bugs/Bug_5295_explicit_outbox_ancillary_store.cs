using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// Regression test for GH-5295, the eager delivery half of GH-4417. Reported by @stigrune-hoc.
//
// The explicit IDbContextOutbox<T> writes the envelope to the ancillary store a DbContext is enrolled
// in, but the DbContextOutbox<T> constructor never bound that store, so delivering eagerly deleted the
// outgoing row from the MAIN store -- zero rows. The envelope stayed in the ancillary store, owned by
// the running node, and was sent a second time by recovery when the node stopped.

public record Probe5295(Guid Id);

public sealed class Business5295DbContext : DbContext
{
    public Business5295DbContext(DbContextOptions<Business5295DbContext> options) : base(options)
    {
    }
}

public sealed class Probe5295Handler
{
    public static int Count;

    public void Handle(Probe5295 message)
    {
        Interlocked.Increment(ref Count);
    }
}

public class Bug_5295_explicit_outbox_ancillary_store
{
    private const string MainSchema = "bug5295_messaging";
    private const string BusinessSchema = "bug5295_business";

    private static Task<IHost> startHostAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Probe5295Handler>();

                opts.Services.AddDbContext<Business5295DbContext>(o => o.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .EnableMessageTransport(t => t.AutoProvision());
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, BusinessSchema,
                        MessageStoreRole.Ancillary)
                    .Enroll<Business5295DbContext>();
                opts.UseEntityFrameworkCoreTransactions();

                opts.ListenAtPort(5295);
                opts.PublishMessage<Probe5295>().ToPort(5295).UseDurableOutbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private static async Task<int> outgoingCountAsync(IMessageStore store)
    {
        return (await store.Admin.AllOutgoingAsync()).Count();
    }

    [Fact]
    public Task eager_flush_deletes_the_outgoing_row_from_the_ancillary_store() =>
        runAsync(async (scope, _) =>
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<Business5295DbContext>>();
            return (outbox, outbox.DbContext);
        });

    [Fact]
    public Task non_generic_outbox_binds_the_ancillary_store_when_enrolled() =>
        runAsync(async (scope, _) =>
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            var dbContext = scope.ServiceProvider.GetRequiredService<Business5295DbContext>();
            outbox.Enroll(dbContext);
            return (outbox, dbContext);
        });

    private static async Task runAsync(Func<AsyncServiceScope, IHost, Task<(IMessageBus outbox, Business5295DbContext db)>> enroll)
    {
        Probe5295Handler.Count = 0;

        using var host = await startHostAsync();
        await host.ResetResourceState(TestContext.Current.CancellationToken);

        var ancillary = host.GetRuntime().Stores.FindAncillaryStore(typeof(Business5295DbContext));

        await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                var (outbox, dbContext) = await enroll(scope, host);

                await using var tx = await dbContext.Database.BeginTransactionAsync();
                await outbox.PublishAsync(new Probe5295(Guid.NewGuid()));
                await dbContext.SaveChangesAsync();
                await tx.CommitAsync();

                // Written in the DbContext's transaction, so it must be in the store the DbContext is enrolled in
                (await outgoingCountAsync(ancillary)).ShouldBe(1, "the envelope was not written to the ancillary store");
                (await outgoingCountAsync(host.GetRuntime().Storage)).ShouldBe(0, "the envelope was written to the main store");

                await ((MessageContext)outbox).FlushOutgoingMessagesAsync();
            }));

        Probe5295Handler.Count.ShouldBe(1);

        // Deleting the sent envelope is asynchronous relative to the tracked session completing
        var remaining = await outgoingCountAsync(ancillary);
        for (var i = 0; i < 50 && remaining > 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            remaining = await outgoingCountAsync(ancillary);
        }

        remaining.ShouldBe(0, "the sent envelope was left in the ancillary store's outgoing table");
    }
}
