using System.Text;
using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
/// redelivery. A JetStream message from a publisher that is not Wolverine carries no <c>id</c> header, so the id it
/// is stored under has to come from the message itself. Otherwise every delivery of it -- after <c>AckWait</c>
/// expired, after a lost ack, or from a consumer replaying the stream -- is a brand new envelope, stored again and
/// handled again.
///
/// <para>
/// The redelivery is forced by deleting the listener's durable consumer while the listener is stopped: the listener
/// creates it again when it starts, and the new consumer delivers the stream from its first message. Both orders a
/// redelivery can arrive in are covered, with and without <c>EnableInboxPartitioning</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// While the first copy is still Incoming. Its handler is held while the listener is paused without draining, the
/// way the inbox-outage handling pauses it.
/// </item>
/// <item>
/// After the first copy was handled, inside <see cref="DurabilitySettings.KeepAfterMessageHandling" />.
/// </item>
/// </list>
/// <para>
/// In both, a sentinel message published behind the redelivered one shows when the redelivery has been dealt with:
/// the listener is sequential, so the sentinel is stored and handled after it.
/// </para>
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class redelivered_jetstream_message_without_id_header_is_handled_once
{
    public enum MessageShape
    {
        // Plain JSON with someone else's headers, and no Wolverine "id" header
        NoIdHeader,

        // The control: a message carrying Wolverine's "id" header, whose id was always stable
        WolverineIdHeader
    }

    public enum Redelivery
    {
        WhileTheFirstCopyIsStillIncoming,
        AfterTheFirstCopyWasHandled
    }

    private readonly ITestOutputHelper _output;

    public redelivered_jetstream_message_without_id_header_is_handled_once(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MessageShape.NoIdHeader, false, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.NoIdHeader, false, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(MessageShape.NoIdHeader, true, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.NoIdHeader, true, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(MessageShape.WolverineIdHeader, false, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.WolverineIdHeader, false, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(MessageShape.WolverineIdHeader, true, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.WolverineIdHeader, true, Redelivery.AfterTheFirstCopyWasHandled)]
    public async Task a_redelivered_message_is_handled_once(MessageShape shape, bool partitionedInbox,
        Redelivery redelivery)
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var run = new TestRun(shape, partitionedInbox);

        var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .UseJetStream(_ => { })
                    .DefineStream(run.StreamName, s => s.WithSubjects(run.Subject));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, run.Schema);
                opts.Durability.EnableInboxPartitioning = partitionedInbox;

                opts.ListenToNatsSubject(run.Subject)
                    .UseJetStream(run.StreamName, run.ConsumerName)
                    .UseDurableInbox()
                    .DefaultIncomingMessage<ForeignOrderPlaced>()
                    .Sequential();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Ct);

        // Disposed asynchronously so the listening agent, and the restart its pause below schedules, go with it
        await using var hostDisposal = (IAsyncDisposable)host;

        // The handled rows have to still be retained when the messages come round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        var agent = host.GetRuntime().Endpoints.FindListeningAgent(run.ListenerUri);
        agent.ShouldNotBeNull();

        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        var js = connection.CreateJetStreamContext();

        if (redelivery == Redelivery.WhileTheFirstCopyIsStillIncoming)
        {
            var firstCopy = ForeignOrderPlacedHandler.HoldTheFirstHandlingOf(run.Id, 1);
            try
            {
                await run.PublishAsync(js, 1);
                await firstCopy.Entered.WaitAsync(60.Seconds(), Ct);

                // A durable listener acks the message once it is in the inbox. Stop the consumer without waiting for
                // the held handler, the way the inbox-outage handling pauses a listener, and have it start over
                await agent.PauseAsync(10.Minutes());
                await run.ReplayTheStreamToTheConsumerAsync(js);
                await agent.StartAsync();

                await run.PublishAsync(js, 2);

                // The redelivered message 1 is ahead of the sentinel in the stream, so once the sentinel is in the
                // inbox, message 1 has been stored again or refused -- while its first copy is still Incoming
                await run.WaitForInboxRowAsync(2);
            }
            finally
            {
                firstCopy.Release();
            }

            // The redelivered copy is handled ahead of the sentinel or refused, and the held first copy has finished:
            // nothing is left in flight that could still handle message 1
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 2);
            await firstCopy.Completed.WaitAsync(60.Seconds(), Ct);
        }
        else
        {
            await run.PublishAsync(js, 1);
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 1);

            // Let the handled mark land before the redelivery arrives
            await run.WaitForHandledRowsAsync(1);

            await agent.StopAndDrainAsync();
            await run.ReplayTheStreamToTheConsumerAsync(js);
            await agent.StartAsync();

            await run.PublishAsync(js, 2);
            await ForeignOrderPlacedHandler.WaitForAsync(run.Id, 2);
        }

        var history = ForeignOrderPlacedHandler.Describe(run.Id);
        _output.WriteLine(history);

        ForeignOrderPlacedHandler.TimesHandled(run.Id, 1).ShouldBe(1, history);
        ForeignOrderPlacedHandler.TimesHandled(run.Id, 2).ShouldBe(1, history);
    }

    private class TestRun
    {
        private readonly bool _withIdHeader;

        public TestRun(MessageShape shape, bool partitionedInbox)
        {
            Id = Guid.NewGuid().ToString("N");
            StreamName = $"FOREIGN_{Id}";
            Subject = $"foreign.{Id}.orders";
            ConsumerName = $"foreign-{Id}";
            Schema = partitionedInbox ? "nats_foreign_redelivery_partitioned" : "nats_foreign_redelivery";
            _withIdHeader = shape == MessageShape.WolverineIdHeader;
        }

        public string Id { get; }
        public string StreamName { get; }
        public string Subject { get; }
        public string ConsumerName { get; }
        public string Schema { get; }

        public Uri ListenerUri => new($"nats://subject/{Subject}");

        public async Task PublishAsync(INatsJSContext js, int sequence)
        {
            // A binary-mode CloudEvent: its own id rides in ce-id, and there is no Wolverine "id" header
            var headers = new NatsHeaders
            {
                ["ce-specversion"] = "1.0",
                ["ce-id"] = $"order-{sequence}",
                ["ce-source"] = "/orders",
                ["ce-type"] = "com.example.order.placed.v1",
                ["content-type"] = "application/json"
            };

            if (_withIdHeader)
            {
                headers[EnvelopeConstants.IdKey] = Guid.NewGuid().ToString();
            }

            var ack = await js.PublishAsync(Subject, JsonSerializer.SerializeToUtf8Bytes(new ForeignOrderPlaced(Id, sequence)),
                headers: headers, cancellationToken: Ct);
            ack.EnsureSuccess();
        }

        /// <summary>
        /// The listener is stopped, so deleting its durable consumer loses nothing but the consumer's position. The
        /// listener creates the consumer again when it starts, and the new one delivers the stream from the start.
        /// </summary>
        public async Task ReplayTheStreamToTheConsumerAsync(INatsJSContext js)
        {
            (await js.DeleteConsumerAsync(StreamName, ConsumerName, Ct)).ShouldBeTrue();
        }

        public Task WaitForInboxRowAsync(int sequence)
        {
            // The body column is the serialized envelope, which carries the message's JSON verbatim
            return waitForRowsAsync("position(@marker in body) > 0",
                cmd => cmd.Parameters.AddWithValue("marker", Encoding.UTF8.GetBytes($"\"Sequence\":{sequence}")), 1,
                $"the inbox row of sequence {sequence}");
        }

        public Task WaitForHandledRowsAsync(int count)
        {
            return waitForRowsAsync("status = 'Handled'", _ => { }, count, $"{count} handled inbox rows");
        }

        private async Task waitForRowsAsync(string predicate, Action<NpgsqlCommand> parameters, int count,
            string description)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (true)
            {
                await using var cmd = new NpgsqlCommand(
                    $"select count(*) from {Schema}.wolverine_incoming_envelopes where received_at = @uri and {predicate}",
                    conn);
                cmd.Parameters.AddWithValue("uri", ListenerUri.ToString());
                parameters(cmd);

                var found = (long)(await cmd.ExecuteScalarAsync())!;
                if (found >= count) return;

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException($"Never found {description}. {ForeignOrderPlacedHandler.Describe(Id)}");
                }

                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}

public record ForeignOrderPlaced(string RunId, int Sequence);

public static class ForeignOrderPlacedHandler
{
    private static readonly object _lock = new();
    private static readonly List<(string RunId, int Sequence, Guid EnvelopeId, string? StreamSequence)> _handled = new();
    private static readonly Dictionary<(string RunId, int Sequence), HeldHandling> _held = new();

    public static async Task Handle(ForeignOrderPlaced message, Envelope envelope)
    {
        HeldHandling? held;
        lock (_lock)
        {
            _held.TryGetValue((message.RunId, message.Sequence), out held);
        }

        var holding = held != null && held.TryEnter();
        if (holding)
        {
            await held!.Released;
        }

        envelope.Headers.TryGetValue("nats-stream-seq", out var streamSequence);
        lock (_lock)
        {
            _handled.Add((message.RunId, message.Sequence, envelope.Id, streamSequence));
        }

        if (holding)
        {
            held!.Complete();
        }
    }

    /// <summary>
    /// The first time this message is handled, its handler waits until released, and its inbox row stays Incoming
    /// </summary>
    public static HeldHandling HoldTheFirstHandlingOf(string runId, int sequence)
    {
        var held = new HeldHandling();
        lock (_lock)
        {
            _held[(runId, sequence)] = held;
        }

        return held;
    }

    public static int TimesHandled(string runId, int sequence)
    {
        lock (_lock)
        {
            return _handled.Count(x => x.RunId == runId && x.Sequence == sequence);
        }
    }

    public static string Describe(string runId)
    {
        lock (_lock)
        {
            return "Handled: " + string.Join(", ",
                _handled.Where(x => x.RunId == runId)
                    .Select(x => $"sequence {x.Sequence} at stream sequence {x.StreamSequence} as envelope {x.EnvelopeId}"));
        }
    }

    public static async Task WaitForAsync(string runId, int sequence)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (TimesHandled(runId, sequence) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Sequence {sequence} of run {runId} was never handled. {Describe(runId)}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

public class HeldHandling
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    public Task Entered => _entered.Task;
    public Task Released => _released.Task;

    // The held handling has been recorded, so the handled count of its message is final once the redelivered copy
    // -- handled or refused -- is accounted for as well
    public Task Completed => _completed.Task;

    public void Complete()
    {
        _completed.TrySetResult();
    }

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref _entries) != 1) return false;

        _entered.TrySetResult();
        return true;
    }

    public void Release()
    {
        _released.TrySetResult();
    }
}
