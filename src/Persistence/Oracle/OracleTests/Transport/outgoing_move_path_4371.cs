using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-4371. <see cref="OracleQueueSender.SendAsync(Envelope)" /> branches on
/// <c>envelope.WasPersistedInOutbox</c>: when the row is already in the outgoing table it MOVES it
/// into the queue table -- one transaction, insert and delete together -- and otherwise it writes
/// straight to the queue table and leaves the outgoing row to be deleted separately afterwards.
///
/// <para>
/// No Oracle store path ever set that flag, so the move branch was unreachable and every durable
/// send took the two-step route: insert into the queue table, then delete from outgoing under a
/// separate connection. A crash between those two leaves the message in the queue AND in outgoing,
/// where the durability agent recovers it and sends it a second time. Every
/// <c>MessageDatabase&lt;T&gt;</c> store (PostgreSQL, SQL Server, MySQL, SQLite) has always stamped
/// the flag; Oracle alone did not.
/// </para>
/// </summary>
[Collection("oracle")]
public class outgoing_move_path_4371 : IAsyncLifetime
{
    private IHost theHost = null!;
    private OracleTransport theTransport = null!;
    private OracleQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport();
                opts.ListenToOracleQueue("gh4371");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        theTransport = theHost.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>();
        theQueue = theTransport.Queues["gh4371"];
        theMessageStore = theHost.GetRuntime().Storage;

        await theQueue.PurgeAsync(NullLogger.Instance);
        await theMessageStore.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static Envelope outgoing()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.WasPersistedInOutbox.ShouldBeFalse("precondition: a fresh envelope has not been stored");
        return envelope;
    }

    /// <summary>
    /// The flag is what the sender branches on, so the store has to set it. This is the whole defect
    /// in one assertion.
    /// </summary>
    [Fact]
    public async Task storing_one_outgoing_envelope_marks_it_as_persisted()
    {
        var envelope = outgoing();

        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 1);

        envelope.WasPersistedInOutbox.ShouldBeTrue();
    }

    /// <summary>
    /// GH-4369 added the batched overload; it has to stamp the flag for the same reason, or a
    /// coalesced send silently drops back to the two-step route that a single send now avoids.
    /// </summary>
    [Fact]
    public async Task storing_a_batch_of_outgoing_envelopes_marks_them_all_as_persisted()
    {
        var envelopes = Enumerable.Range(0, 5).Select(_ => outgoing()).ToArray();

        await theMessageStore.Outbox.StoreOutgoingAsync(envelopes, 1);

        envelopes.ShouldAllBe(x => x.WasPersistedInOutbox);
    }

    /// <summary>
    /// The behaviour the flag buys, end to end: a durable send of an already-stored envelope leaves
    /// the queue row present and the outgoing row GONE, because one transaction did both. Before the
    /// fix the outgoing row survived the send and waited on a separate DELETE.
    /// </summary>
    [Fact]
    public async Task a_durable_send_moves_the_row_out_of_outgoing()
    {
        theQueue.Mode = EndpointMode.Durable;

        var envelope = outgoing();
        envelope.Destination = theQueue.Uri;

        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 1);
        (await theMessageStore.Admin.AllOutgoingAsync()).Count.ShouldBe(1);

        var sender = new OracleQueueSender(theQueue, theQueue.DataSource, null);

        // The no-token overload deliberately: it is the one that branches on WasPersistedInOutbox.
        // SendAsync(envelope, token) is the "write directly to the queue table" arm underneath it, so
        // calling that instead would test the wrong half of the very branch this test is about.
#pragma warning disable xUnit1051
        await sender.SendAsync(envelope);
#pragma warning restore xUnit1051

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theMessageStore.Admin.AllOutgoingAsync())
            .ShouldBeEmpty("the send should have moved the row, not copied it");
    }
}
