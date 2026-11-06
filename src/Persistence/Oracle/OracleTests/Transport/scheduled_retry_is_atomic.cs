using IntegrationTests;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Tracking;

namespace OracleTests.Transport;

/// <summary>
/// A scheduled retry of a message received on an Oracle queue deletes its inbox row and writes it to the
/// queue's scheduled table. Those were two separately committed commands, so a scheduled write that failed -- a command
/// timeout or a deadlock, found by the durability chaos rig -- left the message in neither table, alive only in
/// the receiver's in-memory retry, and a node that died or shut down before that retry succeeded lost it.
/// </summary>
public class scheduled_retry_is_atomic : IAsyncLifetime
{
    private const string QueueName = "schedretry";

    private IHost _host = null!;
    private OracleQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport();

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<OracleScheduledRetryMessage>().ToOracleQueue(QueueName);
            }).StartAsync();

        _queue = _host.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>().Queues[QueueName];
        _queue.Mode = EndpointMode.Durable;
        await _queue.PurgeAsync(NullLogger.Instance);
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// What a durable listener has done by the time the handler fails: the envelope sits in the inbox
    /// </summary>
    private async Task<Envelope> receivedIntoTheInboxAsync()
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(OracleScheduledRetryMessage))
            .RouteForSend(new OracleScheduledRetryMessage(Guid.NewGuid()), null).Single();
        envelope.Destination = _queue.Uri;
        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        return envelope;
    }

    private async Task<bool> isInTheInboxAsync(Envelope envelope) =>
        (await _host.GetRuntime().Storage.Admin.AllIncomingAsync()).Any(x => x.Id == envelope.Id);

    [Fact]
    public async Task a_scheduled_retry_moves_the_inbox_row_to_the_scheduled_table()
    {
        var envelope = await receivedIntoTheInboxAsync();

        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_failed_scheduled_write_leaves_the_inbox_row_in_place()
    {
        var envelope = await receivedIntoTheInboxAsync();

        // Too long for the scheduled table's VARCHAR2(500) message_type, so the write fails with ORA-12899 after the
        // delete -- the test user may not create a trigger in the shared WOLVERINE schema
        var messageType = envelope.MessageType;
        envelope.MessageType = new string('x', 600);

        await Should.ThrowAsync<OracleException>(() => _queue.ScheduleRetryAsync(envelope, CancellationToken.None));

        // Still in the inbox, so recovery runs it again if this node goes away
        (await isInTheInboxAsync(envelope)).ShouldBeTrue();
        (await _queue.ScheduledCountAsync()).ShouldBe(0);

        // and the receiver's retry completes the move once the write can succeed
        envelope.MessageType = messageType;
        await _queue.ScheduleRetryAsync(envelope, CancellationToken.None);

        (await isInTheInboxAsync(envelope)).ShouldBeFalse();
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }
}

public record OracleScheduledRetryMessage(Guid Id);
