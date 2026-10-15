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
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// Regression test for GH-5040. Reported by @stigrune-hoc.
//
// A durable PostgreSQL queue moves each row into the MAIN store's inbox as it dequeues it, so the
// receiver never decides which store the row goes to. The handler takes a DbContext enrolled in an
// ancillary store on another database (another schema here, which is a different store all the same),
// and EfCoreEnvelopeTransaction marked the envelope handled in THAT database -- zero rows -- and then set
// Status = Handled, so DurableReceiver skipped the mark that would have found the row. The row stayed
// Incoming in the main store and was replayed on every restart.

public record Reply5040(Guid Id);

public sealed class Business5040DbContext : DbContext
{
    public Business5040DbContext(DbContextOptions<Business5040DbContext> options) : base(options)
    {
    }
}

public sealed class Reply5040Handler
{
    public static int Count;

    // Taking the DbContext puts the chain under the EF Core transaction middleware
    public void Handle(Reply5040 message, Business5040DbContext db)
    {
        Interlocked.Increment(ref Count);
    }
}

public class Bug_5040_postgresql_queue_with_ancillary_dbcontext
{
    private const string MainSchema = "bug5040_messaging";
    private const string BusinessSchema = "bug5040_business";

    private static Task<IHost> startHostAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Reply5040Handler>();

                opts.Services.AddDbContext<Business5040DbContext>(o => o.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .EnableMessageTransport(t => t.AutoProvision());
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, BusinessSchema,
                        MessageStoreRole.Ancillary)
                    .Enroll<Business5040DbContext>();
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.ListenToPostgresqlQueue("bug5040_replies").UseDurableInbox();
                opts.PublishMessage<Reply5040>().ToPostgresqlQueue("bug5040_replies");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private static async Task<int> incomingCountAsync(IMessageStore store)
    {
        var messageType = typeof(Reply5040).ToMessageTypeName();
        return (await store.Admin.AllIncomingAsync())
            .Count(x => x.MessageType == messageType && x.Status == EnvelopeStatus.Incoming);
    }

    [Fact]
    public async Task envelope_is_marked_handled_in_the_store_that_holds_it_and_not_replayed()
    {
        Reply5040Handler.Count = 0;

        using (var host = await startHostAsync())
        {
            await host.ResetResourceState(TestContext.Current.CancellationToken);

            await host.TrackActivity()
                .IncludeExternalTransports()
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(new Reply5040(Guid.NewGuid()));

            Reply5040Handler.Count.ShouldBe(1);

            // Marking the envelope handled is asynchronous relative to the tracked session completing
            var main = host.GetRuntime().Storage;
            var stranded = await incomingCountAsync(main);
            for (var i = 0; i < 50 && stranded > 0; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                stranded = await incomingCountAsync(main);
            }

            stranded.ShouldBe(0, "the envelope was left Incoming in the main store that holds its row");

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var host = await startHostAsync())
        {
            // Inbox recovery replays any row left Incoming here
            await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        Reply5040Handler.Count.ShouldBe(1, "a handled message was replayed after a restart");
    }
}
