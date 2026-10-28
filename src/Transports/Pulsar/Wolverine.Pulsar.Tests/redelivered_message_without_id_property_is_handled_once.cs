using System.Buffers;
using System.Text.Json;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
/// redelivery. A message from a producer that is not Wolverine carries no <c>id</c> property, so the id it is
/// stored under has to come from the message itself -- its topic, its <see cref="MessageId" /> and the
/// subscription reading it. Otherwise every delivery of it -- an ack timeout, a negative ack, a consumer
/// restart, a subscription reset to an earlier position -- is a brand new envelope, stored again and handled
/// again.
///
/// <para>
/// The redelivery here is the broker's own: the subscription's cursor is reset to the beginning of the topic
/// through the Pulsar admin API, which disconnects the listener's consumer and makes the broker deliver every
/// message again once it reconnects. Both orders a redelivery can arrive in are covered:
/// </para>
/// <list type="bullet">
/// <item>While the first copy is still Incoming, because its handler is held.</item>
/// <item>After the first copy was handled, inside <see cref="DurabilitySettings.KeepAfterMessageHandling" />.</item>
/// </list>
/// <para>
/// In both, a sentinel message produced behind the redelivered one shows when the redelivery has been dealt
/// with: the topic delivers in order and the listener is sequential, so the sentinel is stored and handled
/// after it.
/// </para>
/// </summary>
[Collection("acceptance")]
public class redelivered_message_without_id_property_is_handled_once
{
    public enum MessageShape
    {
        // No "id" property: the properties are someone else's
        NoIdProperty,

        // The control: a message carrying Wolverine's "id" property, whose id was always stable
        WolverineIdProperty
    }

    public enum Redelivery
    {
        WhileTheFirstCopyIsStillIncoming,
        AfterTheFirstCopyWasHandled
    }

    [Theory]
    [InlineData(MessageShape.NoIdProperty, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.NoIdProperty, Redelivery.AfterTheFirstCopyWasHandled)]
    [InlineData(MessageShape.WolverineIdProperty, Redelivery.WhileTheFirstCopyIsStillIncoming)]
    [InlineData(MessageShape.WolverineIdProperty, Redelivery.AfterTheFirstCopyWasHandled)]
    public async Task a_redelivered_message_is_handled_once(MessageShape shape, Redelivery redelivery)
    {
        var run = new TestRun(shape);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "pulsar_foreign_redelivery");

                opts.ListenToPulsarTopic(run.TopicPath)
                    .SubscriptionName(run.SubscriptionName)
                    .DefaultIncomingMessage<ForeignPulsarOrderPlaced>()
                    .UseDurableInbox()
                    .Sequential();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignPulsarOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await using var hostDisposal = (IAsyncDisposable)host;

        // The handled rows have to still be retained when the messages come round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        // A plain Pulsar producer, not Wolverine
        await using var client = PulsarClient.Builder().ServiceUrl(PulsarContainerFixture.ServiceUrl).Build();
        await using var producer = client.NewProducer().Topic(run.TopicPath).Create();

        if (redelivery == Redelivery.WhileTheFirstCopyIsStillIncoming)
        {
            var firstCopy = ForeignPulsarOrderPlacedHandler.HoldTheFirstHandlingOf(run.Id, 1);
            try
            {
                await run.ProduceAsync(producer, 1);
                await firstCopy.Entered.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

                // A durable listener acknowledges the message once it is in the inbox, so the broker has
                // already let go of it. Rewinding the subscription makes the broker deliver it again while
                // the first copy is still being handled
                await run.ResetTheSubscriptionToTheBeginningAsync();

                await run.ProduceAsync(producer, 2);

                // The redelivered message 1 is ahead of the sentinel on the topic, so once the sentinel is in the
                // inbox, message 1 has been stored again or refused -- while its first copy is still Incoming
                await run.WaitForInboxRowAsync(2);
            }
            finally
            {
                firstCopy.Release();
            }

            // The redelivered copy is handled ahead of the sentinel or refused, and the held first copy has
            // finished: nothing is left in flight that could still handle message 1
            await ForeignPulsarOrderPlacedHandler.WaitForAsync(run.Id, 2);
            await firstCopy.Completed.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);
        }
        else
        {
            await run.ProduceAsync(producer, 1);
            await ForeignPulsarOrderPlacedHandler.WaitForAsync(run.Id, 1);

            // Let the handled mark land before the redelivery arrives
            await run.WaitForHandledRowsAsync(1);

            await run.ResetTheSubscriptionToTheBeginningAsync();

            await run.ProduceAsync(producer, 2);
            await ForeignPulsarOrderPlacedHandler.WaitForAsync(run.Id, 2);
        }

        var history = ForeignPulsarOrderPlacedHandler.Describe(run.Id);
        ForeignPulsarOrderPlacedHandler.TimesHandled(run.Id, 1).ShouldBe(1, history);
        ForeignPulsarOrderPlacedHandler.TimesHandled(run.Id, 2).ShouldBe(1, history);
    }

    private class TestRun
    {
        private const string Schema = "pulsar_foreign_redelivery";
        private readonly bool _withIdProperty;

        public TestRun(MessageShape shape)
        {
            Id = Guid.NewGuid().ToString("N");
            TopicName = $"foreign-redelivery-{Id}";
            SubscriptionName = $"foreign-redelivery-{Id}";
            _withIdProperty = shape == MessageShape.WolverineIdProperty;
        }

        public string Id { get; }
        public string TopicName { get; }
        public string TopicPath => $"persistent://public/default/{TopicName}";
        public string SubscriptionName { get; }

        private string listenerUri => PulsarEndpointUri.Topic(TopicPath).ToString();

        public async Task ProduceAsync(IProducer<ReadOnlySequence<byte>> producer, int sequence)
        {
            // A binary-mode CloudEvent: its own id rides in ce_id, and there is no Wolverine "id" property
            var metadata = new MessageMetadata { Key = $"order-{sequence}" };
            metadata["ce_specversion"] = "1.0";
            metadata["ce_id"] = $"order-{sequence}";
            metadata["ce_source"] = "/orders";
            metadata["ce_type"] = "com.example.order.placed.v1";
            metadata["content-type"] = "application/json";

            if (_withIdProperty)
            {
                metadata[EnvelopeConstants.IdKey] = Guid.NewGuid().ToString();
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(new ForeignPulsarOrderPlaced(Id, sequence));
            await producer.Send(metadata, new ReadOnlySequence<byte>(body));
        }

        /// <summary>
        /// Moves the subscription's cursor back to the first message of the topic. The broker disconnects the
        /// subscription's consumer to do it, and delivers everything again to the consumer that reconnects.
        /// </summary>
        public async Task ResetTheSubscriptionToTheBeginningAsync()
        {
            using var http = new HttpClient();
            var url =
                $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/persistent/public/default/{TopicName}/subscription/{SubscriptionName}/resetcursor/0";

            var response = await http.PostAsync(url, null);

            // Without this the test could pass without anything having been redelivered at all
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        }

        public Task WaitForInboxRowAsync(int sequence)
        {
            // The body column is the serialized envelope, which carries the message's JSON verbatim
            return waitForRowsAsync("position(@marker in body) > 0",
                cmd => cmd.Parameters.AddWithValue("marker",
                    System.Text.Encoding.UTF8.GetBytes($"\"Sequence\":{sequence}")), 1,
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
                cmd.Parameters.AddWithValue("uri", listenerUri);
                parameters(cmd);

                var found = (long)(await cmd.ExecuteScalarAsync())!;
                if (found >= count) return;

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Never found {description}. {ForeignPulsarOrderPlacedHandler.Describe(Id)}");
                }

                await Task.Delay(100);
            }
        }
    }
}

public record ForeignPulsarOrderPlaced(string RunId, int Sequence);

public static class ForeignPulsarOrderPlacedHandler
{
    private static readonly object _lock = new();
    private static readonly List<(string RunId, int Sequence, Guid EnvelopeId)> _handled = new();
    private static readonly Dictionary<(string RunId, int Sequence), HeldPulsarHandling> _held = new();

    public static async Task Handle(ForeignPulsarOrderPlaced message, Envelope envelope)
    {
        HeldPulsarHandling? held;
        lock (_lock)
        {
            _held.TryGetValue((message.RunId, message.Sequence), out held);
        }

        var holding = held != null && held.TryEnter();
        if (holding)
        {
            await held!.Released;
        }

        lock (_lock)
        {
            _handled.Add((message.RunId, message.Sequence, envelope.Id));
        }

        if (holding)
        {
            held!.Complete();
        }
    }

    /// <summary>
    /// The first time this message is handled, its handler waits until released, and its inbox row stays Incoming
    /// </summary>
    public static HeldPulsarHandling HoldTheFirstHandlingOf(string runId, int sequence)
    {
        var held = new HeldPulsarHandling();
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
                    .Select(x => $"sequence {x.Sequence} as envelope {x.EnvelopeId}"));
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

            await Task.Delay(100);
        }
    }
}

public class HeldPulsarHandling
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    public Task Entered => _entered.Task;
    public Task Released => _released.Task;

    // The held handling has been recorded, so the handled count of its message is final once the redelivered
    // copy -- handled or refused -- is accounted for as well
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
