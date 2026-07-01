using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Integration coverage for per-message dynamic NATS subjects. Two mechanisms are exercised, both built on
/// Wolverine's generic topic routing (<c>RoutingMode.ByTopic</c> / <c>Envelope.TopicName</c>):
/// <list type="bullet">
/// <item>the strongly-typed <c>PublishMessagesToNatsSubject&lt;T&gt;(Func&lt;T,string&gt;)</c> registration, and</item>
/// <item><c>IMessageBus.BroadcastToTopicAsync</c>, which the same ByTopic endpoint auto-enrolls in.</item>
/// </list>
/// A third test covers the advanced <see cref="ISubjectResolver"/> escape hatch that rewrites the subject from
/// envelope-level state a strongly-typed function can't reach.
///
/// Each test proves two things: a <c>{root}.&gt;</c> wildcard Wolverine listener consumes the message
/// end-to-end (tracking), and a raw NATS subscriber bound to the <em>exact</em> expected subject receives it —
/// the raw subscriber is what pins down the concrete subject, since the receiving pipeline overwrites
/// <c>Envelope.Destination</c> with the listener's own (wildcard) address.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDynamicSubjectTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _sender;
    private IHost? _receiver;
    private string _root = null!;
    private string _natsUrl = null!;

    public NatsDynamicSubjectTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _natsUrl = NatsTestHelpers.ResolveUrl();
        _root = $"orders.events.{Guid.NewGuid():N}";

        if (!await NatsTestHelpers.IsNatsAvailable(_natsUrl))
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        _receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DynamicSubjectReceiver";
                opts.UseNats(_natsUrl).AutoProvision();

                // One wildcard Core NATS listener captures every per-message subject under the root.
                opts.ListenToNatsSubject($"{_root}.>").Named("wildcard");
            })
            .StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DynamicSubjectSender";
                opts.UseNats(_natsUrl).AutoProvision();

                // The sender also discovers OrderPlacedHandler (same assembly); without this the additive
                // TopicRouting source AND the local handler would both fire, double-counting in tracking.
                opts.Policies.DisableConventionalLocalRouting();

                // Per-message subject computed from the message body.
                opts.PublishMessagesToNatsSubject<OrderPlaced>(m => $"{_root}.{m.OrderId}").SendInline();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }

        if (_receiver != null)
        {
            await _receiver.StopAsync();
            _receiver.Dispose();
        }
    }

    [Fact]
    public async Task publishes_to_a_per_message_subject_computed_from_the_body()
    {
        if (_sender == null || _receiver == null)
        {
            return;
        }

        var orderId = Guid.NewGuid().ToString("N");
        var expectedSubject = $"{_root}.{orderId}";

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_natsUrl, expectedSubject);

        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new OrderPlaced(orderId));

        // The wildcard Wolverine listener consumed it end-to-end.
        session.Received.SingleMessage<OrderPlaced>().OrderId.ShouldBe(orderId);

        // ...and it landed on exactly the computed subject.
        var delivered = await raw.ReadAsync(5.Seconds());
        delivered.ShouldNotBeNull();
        delivered!.Value.Subject.ShouldBe(expectedSubject);
    }

    [Fact]
    public async Task two_messages_land_on_two_distinct_computed_subjects()
    {
        if (_sender == null || _receiver == null)
        {
            return;
        }

        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");

        await using var rawFirst = await NatsTestHelpers.SubscribeRawAsync(_natsUrl, $"{_root}.{firstId}");
        await using var rawSecond = await NatsTestHelpers.SubscribeRawAsync(_natsUrl, $"{_root}.{secondId}");

        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async context =>
            {
                await context.SendAsync(new OrderPlaced(firstId));
                await context.SendAsync(new OrderPlaced(secondId));
            }));

        session.Received.MessagesOf<OrderPlaced>().Count().ShouldBe(2);

        var first = await rawFirst.ReadAsync(5.Seconds());
        first.ShouldNotBeNull();
        first!.Value.Subject.ShouldBe($"{_root}.{firstId}");

        var second = await rawSecond.ReadAsync(5.Seconds());
        second.ShouldNotBeNull();
        second!.Value.Subject.ShouldBe($"{_root}.{secondId}");
    }

    [Fact]
    public async Task broadcast_to_topic_async_publishes_to_the_explicit_subject()
    {
        if (_sender == null || _receiver == null)
        {
            return;
        }

        // BroadcastToTopicAsync routes to every ByTopic endpoint (the one created by
        // PublishMessagesToNatsSubject above) with an explicit topic that overrides the Func.
        var subject = $"{_root}.broadcast";

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_natsUrl, subject);

        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(context =>
                context.BroadcastToTopicAsync(subject, new OrderPlaced("broadcast")));

        session.Received.SingleMessage<OrderPlaced>().OrderId.ShouldBe("broadcast");

        var delivered = await raw.ReadAsync(5.Seconds());
        delivered.ShouldNotBeNull();
        delivered!.Value.Subject.ShouldBe(subject);
    }

    [Fact]
    public async Task subject_resolver_escape_hatch_rewrites_the_subject_from_envelope_state()
    {
        if (!await NatsTestHelpers.IsNatsAvailable(_natsUrl))
        {
            return;
        }

        var root = $"aggregates.{Guid.NewGuid():N}";
        var baseSubject = $"{root}.base";
        var expectedSubject = $"{baseSubject}.A-42";

        using var receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ResolverReceiver";
                opts.UseNats(_natsUrl).AutoProvision();
                opts.ListenToNatsSubject($"{root}.>").Named("resolver-wildcard");
            })
            .StartAsync();

        using var sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ResolverSender";
                opts.UseNats(natsCfg =>
                {
                    natsCfg.ConnectionString = _natsUrl;
                    // Advanced hook: rewrite the outgoing subject from an envelope header the typed
                    // Func<T,string> can't see.
                    natsCfg.SubjectResolver = new AggregateSubjectResolver();
                }).AutoProvision();

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(baseSubject).SendInline();
            })
            .StartAsync();

        await using var raw = await NatsTestHelpers.SubscribeRawAsync(_natsUrl, expectedSubject);

        var session = await sender
            .TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new OrderPlaced("A-42"),
                new DeliveryOptions { Headers = { ["aggregate-id"] = "A-42" } });

        session.Received.SingleMessage<OrderPlaced>().OrderId.ShouldBe("A-42");

        var delivered = await raw.ReadAsync(5.Seconds());
        delivered.ShouldNotBeNull();
        delivered!.Value.Subject.ShouldBe(expectedSubject);
    }

    /// <summary>
    /// Rewrites the base subject to <c>{base}.{aggregate-id}</c> using the <c>aggregate-id</c> header —
    /// the kind of envelope-level shaping the strongly-typed subject function can't express.
    /// </summary>
    private sealed class AggregateSubjectResolver : ISubjectResolver
    {
        public string ResolveSubject(string baseSubject, Envelope envelope)
        {
            return envelope.Headers.TryGetValue("aggregate-id", out var id) && id.IsNotEmpty()
                ? $"{baseSubject}.{id}"
                : baseSubject;
        }

        public string? ExtractTenantId(string subject) => null;
    }
}
