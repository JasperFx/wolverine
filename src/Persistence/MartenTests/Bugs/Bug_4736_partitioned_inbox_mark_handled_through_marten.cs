using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace MartenTests.Bugs;

// Regression test for GH-4736, a 6.44.0 regression on top of GH-4705.
//
// With EnableInboxPartitioning the mark-as-handled command is TWO statements -- a DELETE that retires the
// incoming row when a Handled row for the identity already exists, then the UPDATE that flips status when it
// does not. GH-4705 moved the statement Marten queues into its session batch out of this listener and into
// MessageDatabase<T>, which is right, but handed it over as one ';'-joined string. Marten's QueueSqlCommand
// rejects any SQL containing a ';':
//
//     System.ArgumentOutOfRangeException: You must specify one SQL command at a time because of Marten's
//     usage of command batching. ';' cannot be used as a command separator here. (Parameter 'sql')
//
// So EVERY durable-inbox message whose handler committed through a Marten session was dead-lettered. The
// reporter saw ~140,000 envelopes in wolverine_dead_letters within 20 minutes of rolling 6.44.0 out.
//
// Nothing in the suite reached it: only a handler running a message off the durable inbox takes the branch at
// all (WasPersistedInInbox). An inline invoke or an HTTP endpoint has _context.Envelope == null and skips it,
// and the GH-4705 tests build the command directly rather than committing a session through it.
//
// So this test has to go the whole way: a durable LOCAL queue (which persists the envelope before executing
// it), a handler that takes an IDocumentSession, AutoApplyTransactions to commit it, and partitioning on.

public record PartitionedInboxMessage(Guid Id);

public sealed class PartitionedInboxDoc
{
    public Guid Id { get; set; }
}

public static class PartitionedInboxMessageHandler
{
    // The IDocumentSession is the point: AutoApplyTransactions commits it, which is what runs
    // FlushOutgoingMessagesOnCommit.BeforeSaveChangesAsync and queues the mark-handled command.
    public static void Handle(PartitionedInboxMessage message, IDocumentSession session)
    {
        session.Store(new PartitionedInboxDoc { Id = message.Id });
    }
}

public class Bug_4736_partitioned_inbox_mark_handled_through_marten : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Other handlers in this assembly need persistence this host does not register
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PartitionedInboxMessageHandler));

                // THE combination that breaks: the partition-aware mark-as-handled is two statements
                opts.Durability.EnableInboxPartitioning = true;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug_4736";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug_4736_wolverine");

                // The envelope has to be persisted in the inbox before the handler runs, or
                // FlushOutgoingMessagesOnCommit skips the branch entirely
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_durable_inbox_message_is_handled_rather_than_dead_lettered()
    {
        var message = new PartitionedInboxMessage(Guid.NewGuid());

        // Not InvokeAsync: an inline invoke has no Envelope on the context, so it never reaches the defect
        var session = await _host.SendMessageAndWaitAsync(message);

        // The exception arrived on SaveChangesAsync, so the handler "succeeded" in every way except this
        session.AllExceptions().ShouldBeEmpty();

        await using var querySession = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await querySession.LoadAsync<PartitionedInboxDoc>(message.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        var storage = _host.GetRuntime().Storage;
        var messageTypeName = typeof(PartitionedInboxMessage).ToMessageTypeName();

        (await storage.Admin.AllIncomingAsync())
            .Where(x => x.MessageType == messageTypeName)
            .ShouldAllBe(x => x.Status == EnvelopeStatus.Handled);

        // The 6.44.0 failure mode, stated outright
        var deadLetters = await storage.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { MessageType = messageTypeName }, TestContext.Current.CancellationToken);

        deadLetters.Envelopes.ShouldBeEmpty(
            "A durable-inbox message committing through a Marten session was dead-lettered by the ';' in the partition-aware mark-as-handled SQL");
    }
}
