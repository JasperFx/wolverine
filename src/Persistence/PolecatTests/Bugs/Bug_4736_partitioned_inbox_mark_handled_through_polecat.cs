using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PolecatTests.Bugs;

// The Polecat half of GH-4736. The issue asked whether Polecat and Fisher were affected too, and the answer
// has to be a test rather than an argument.
//
// Under EnableInboxPartitioning the mark-incoming-as-handled command that MessageDatabase<T> builds is TWO
// statements -- a DELETE then an UPDATE. On Marten that broke outright: QueueSqlCommand rejects any SQL
// containing a ';', so every durable-inbox message committing through a Marten session was dead-lettered.
//
// Polecat does not queue the command into a Marten-style batch; MarkIncomingAsHandledParticipant puts the
// whole thing on one SqlCommand.CommandText, which T-SQL happily runs as a multi-statement batch, with the
// named parameters bound once applying to both statements. And Polecat's store is SQL Server, where the
// partitioned incoming table does not exist, so the DELETE cannot match a row: the primary key allows only
// one row per inbox identity, and that row cannot be non-Handled while a Handled row for the same identity
// also exists.
//
// Both of those are claims about behaviour at commit time on a path only durable-inbox traffic reaches, which
// is exactly the kind of claim GH-4736 was. So assert it: turn the setting on, run a message through the
// durable inbox with a handler that commits a Polecat session, and require that it is handled rather than
// dead-lettered.
public record PartitionedPolecatInboxMessage(Guid Id);

public class PartitionedPolecatInboxDoc
{
    public Guid Id { get; set; }
}

public static class PartitionedPolecatInboxMessageHandler
{
    // The IDocumentSession is the point: AutoApplyTransactions commits it, and the commit is what enlists
    // MarkIncomingAsHandledParticipant and runs the mark-as-handled command.
    public static void Handle(PartitionedPolecatInboxMessage message, IDocumentSession session)
    {
        session.Store(new PartitionedPolecatInboxDoc { Id = message.Id });
    }
}

public class Bug_4736_partitioned_inbox_mark_handled_through_polecat : IAsyncLifetime
{
    private const string Schema = "bug_4736_pc";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PartitionedPolecatInboxMessageHandler));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = Schema;
                    })
                    .IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;

                // THE setting. Partitioning itself is PostgreSQL-only, but this flag is global, so a Polecat
                // application can set it and get the two-statement command against a SQL Server inbox.
                opts.Durability.EnableInboxPartitioning = true;

                // The envelope has to be persisted in the inbox before the handler runs, or
                // FlushOutgoingMessagesOnCommit skips the mark-handled branch entirely
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();

        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_durable_inbox_message_is_handled_rather_than_dead_lettered()
    {
        var message = new PartitionedPolecatInboxMessage(Guid.NewGuid());

        // Not InvokeAsync: an inline invoke has no Envelope on the context, so it never reaches the branch
        var session = await _host.SendMessageAndWaitAsync(message);

        session.AllExceptions().ShouldBeEmpty();

        await using var querySession = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await querySession.LoadAsync<PartitionedPolecatInboxDoc>(message.Id,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();

        var storage = _host.GetRuntime().Storage;
        var messageTypeName = typeof(PartitionedPolecatInboxMessage).ToMessageTypeName();

        // The DELETE half of the partitioned command must not have retired the row on the way past: it is
        // the UPDATE that has to settle it, and the row has to end up Handled rather than simply gone
        var incoming = (await storage.Admin.AllIncomingAsync())
            .Where(x => x.MessageType == messageTypeName)
            .ToArray();

        // ShouldAllBe is vacuous on an empty array
        incoming.ShouldNotBeEmpty();
        incoming.ShouldAllBe(x => x.Status == EnvelopeStatus.Handled);

        var deadLetters = await storage.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { MessageType = messageTypeName }, TestContext.Current.CancellationToken);

        deadLetters.Envelopes.ShouldBeEmpty(
            "A durable-inbox message committing through a Polecat session was dead-lettered by the partition-aware mark-as-handled command");
    }
}
