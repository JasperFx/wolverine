using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Pubsub.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
///     The durable inbox deduplicates on <see cref="Envelope.Id" />, and
///     <see cref="DurabilitySettings.KeepAfterMessageHandling" /> keeps a handled row around to absorb a broker
///     redelivery. A message from a publisher other than Wolverine carries no Wolverine <c>id</c> attribute, so the id
///     it is stored under has to come from the Pub/Sub message itself. Otherwise every delivery of it is a brand new
///     envelope, and a redelivery -- an ack deadline that ran out, a subscriber that went away before its ack reached
///     Pub/Sub, a seek back in time -- is stored again and handled again.
///     <para>
///         The redelivery here is a seek: the subscription retains acknowledged messages, and once the message has
///         been handled the subscription is moved back to before it was published, so Pub/Sub delivers it again.
///     </para>
/// </summary>
public class redelivered_message_without_id_attribute_is_handled_once
{
    public enum MessageShape
    {
        // Published by someone else: the attributes are theirs, and there is no Wolverine "id"
        NoIdAttribute,

        // The control: a message carrying Wolverine's "id" attribute, whose id was always stable
        WolverineIdAttribute
    }

    [Theory]
    [InlineData(MessageShape.NoIdAttribute)]
    [InlineData(MessageShape.WolverineIdAttribute)]
    public async Task a_message_redelivered_after_it_was_handled_is_handled_once(MessageShape shape)
    {
        if (!await TestingExtensions.IsEmulatorAvailable()) return;

        var run = new TestRun(shape);
        var duplicates = new DuplicateDeliveryRecorder();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(duplicates))
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TestRun.Schema);

                opts.ListenToPubsubTopic(run.TopicName)
                    .DefaultIncomingMessage<ForeignOrderPlaced>()
                    // So that the subscription can be moved back to before a message it has already acked
                    .ConfigurePubsubSubscription(x => x.RetainAckedMessages = true)
                    .UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ForeignOrderPlacedHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        // The handled row has to still be retained when the message comes round again
        host.GetRuntime().DurabilitySettings.KeepAfterMessageHandling.ShouldBeGreaterThan(1.Minutes());

        var subscription = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>()
            .Topics[run.TopicName].Server.Subscription.Name;

        var beforePublishing = DateTime.UtcNow.AddSeconds(-5);
        var messageId = await run.PublishAsync();

        await ForeignOrderPlacedHandler.WaitForAsync(run.Id);
        await run.WaitForHandledRowAsync();

        // Pub/Sub delivers every message published since then again, including the one already handled
        await run.SeekAsync(subscription, beforePublishing);

        // Either the redelivery is handled a second time, or the inbox refuses it as a duplicate
        await waitForAsync(
            () => ForeignOrderPlacedHandler.TimesHandled(run.Id) > 1 || duplicates.Count > 0,
            "the redelivery was never handled or refused");

        var history = ForeignOrderPlacedHandler.Describe(run.Id);
        ForeignOrderPlacedHandler.TimesHandled(run.Id).ShouldBe(1, history);

        if (shape == MessageShape.NoIdAttribute)
        {
            // The same id a push subscription delivering this message would give it
            ForeignOrderPlacedHandler.EnvelopeIds(run.Id)
                .ShouldAllBe(x => x == PubsubMessageIdentity.IdFor(subscription.ToString(), messageId));
        }
    }

    private static async Task waitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private class TestRun
    {
        public const string Schema = "pubsub_foreign_redelivery";

        private readonly MessageShape _shape;

        public TestRun(MessageShape shape)
        {
            _shape = shape;
            Id = Guid.NewGuid().ToString("N");
            TopicName = $"foreign-redelivery-{Id}";
        }

        public string Id { get; }
        public string TopicName { get; }

        private string listenerUri => $"pubsub://wolverine/{TopicName}";

        /// <summary>
        ///     Publish the order the way any other Pub/Sub client would, and answer the Pub/Sub message id
        /// </summary>
        public async Task<string> PublishAsync()
        {
            var publisher = await new PublisherServiceApiClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly
            }.BuildAsync();

            var message = new PubsubMessage
            {
                Data = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new ForeignOrderPlaced(Id))),
                Attributes = { ["source"] = "/orders", ["content-type"] = "application/json" }
            };

            if (_shape == MessageShape.WolverineIdAttribute)
            {
                message.Attributes[EnvelopeConstants.IdKey] = Guid.NewGuid().ToString();
            }

            var response = await publisher.PublishAsync(new TopicName("wolverine", TopicName), [message]);
            return response.MessageIds.Single();
        }

        public async Task SeekAsync(SubscriptionName subscription, DateTime time)
        {
            var subscriber = await new SubscriberServiceApiClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly
            }.BuildAsync();

            await subscriber.SeekAsync(new SeekRequest
            {
                SubscriptionAsSubscriptionName = subscription,
                Time = Timestamp.FromDateTime(time)
            });
        }

        public async Task WaitForHandledRowAsync()
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (true)
            {
                await using var cmd = new NpgsqlCommand(
                    $"select count(*) from {Schema}.wolverine_incoming_envelopes where received_at = @uri and status = 'Handled'",
                    conn);
                cmd.Parameters.AddWithValue("uri", listenerUri);

                if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"The handled inbox row never appeared. {ForeignOrderPlacedHandler.Describe(Id)}");
                }

                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}

/// <summary>
///     Counts the deliveries the durable inbox refused because it already held them
/// </summary>
internal class DuplicateDeliveryRecorder : ILoggerProvider
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName)
    {
        return new Recorder(this);
    }

    public void Dispose()
    {
    }

    private class Recorder : ILogger
    {
        private readonly DuplicateDeliveryRecorder _parent;

        public Recorder(DuplicateDeliveryRecorder parent)
        {
            _parent = parent;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Discarding a duplicate delivery"))
            {
                Interlocked.Increment(ref _parent._count);
            }
        }
    }
}

public record ForeignOrderPlaced(string RunId);

public static class ForeignOrderPlacedHandler
{
    private static readonly object _lock = new();
    private static readonly List<(string RunId, Guid EnvelopeId)> _handled = new();

    public static void Handle(ForeignOrderPlaced message, Envelope envelope)
    {
        lock (_lock)
        {
            _handled.Add((message.RunId, envelope.Id));
        }
    }

    public static int TimesHandled(string runId)
    {
        lock (_lock)
        {
            return _handled.Count(x => x.RunId == runId);
        }
    }

    public static Guid[] EnvelopeIds(string runId)
    {
        lock (_lock)
        {
            return _handled.Where(x => x.RunId == runId).Select(x => x.EnvelopeId).ToArray();
        }
    }

    public static string Describe(string runId)
    {
        return "Handled as envelopes: " + string.Join(", ", EnvelopeIds(runId));
    }

    public static async Task WaitForAsync(string runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (TimesHandled(runId) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Run {runId} was never handled");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
