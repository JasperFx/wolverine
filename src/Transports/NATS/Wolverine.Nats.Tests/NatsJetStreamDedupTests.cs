using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Integration coverage for server-side JetStream deduplication. Wolverine now stamps a
/// <c>Nats-Msg-Id</c> on every JetStream publish (default: the envelope Id, overridable via
/// <c>DeduplicateUsing</c> or an explicit <c>Nats-Msg-Id</c> header), so the stream's duplicate
/// window actually discards duplicates — the idempotency guarantee external (non-Wolverine)
/// consumers rely on.
///
/// Assertions are deterministic: each publish goes through an inline JetStream sender that awaits the
/// publish-ack, so by the time the send returns the message is either persisted or discarded, and the
/// stream's message count is authoritative.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsJetStreamDedupTests
{
    // The NATS JetStream server-side dedup header (mirrors JetStreamPublisher.NatsMsgIdHeader, which is internal).
    private const string NatsMsgIdHeader = "Nats-Msg-Id";

    private readonly ITestOutputHelper _output;

    public NatsJetStreamDedupTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task same_domain_msg_id_is_deduplicated_by_the_stream()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var stream = $"DEDUP_{Guid.NewGuid():N}";
        var subject = $"dedup.domain.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DedupDomain";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    // Project a stable domain identity into the dedup key instead of the per-send envelope Id.
                    .DeduplicateUsing(e => ((OrderPlaced)e.Message!).OrderId)
                    .DefineStream(stream, s => s
                        .WithSubjects(subject)
                        .WithDeduplicationWindow(5.Minutes()));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).SendInline();
            })
            .StartAsync();

        var bus = host.MessageBus();
        var orderId = Guid.NewGuid().ToString("N");

        await bus.SendAsync(new OrderPlaced(orderId));                          // persisted
        await bus.SendAsync(new OrderPlaced(orderId));                          // same key -> discarded
        await bus.SendAsync(new OrderPlaced(Guid.NewGuid().ToString("N")));     // different key -> persisted

        (await CountStreamMessagesAsync(natsUrl, stream)).ShouldBe(2);
    }

    [Fact]
    public async Task explicit_nats_msg_id_header_is_honored_for_dedup()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var stream = $"DEDUP_{Guid.NewGuid():N}";
        var subject = $"dedup.header.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DedupHeader";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s
                        .WithSubjects(subject)
                        .WithDeduplicationWindow(5.Minutes()));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).SendInline();
            })
            .StartAsync();

        var bus = host.MessageBus();
        var fixedMsgId = Guid.NewGuid().ToString("N");

        // Two logically-different messages (distinct envelope Ids) but the same explicit Nats-Msg-Id header;
        // the explicit header must win over the default and be honored by the server for dedup.
        await bus.SendAsync(new OrderPlaced("a"),
            new DeliveryOptions { Headers = { [NatsMsgIdHeader] = fixedMsgId } });
        await bus.SendAsync(new OrderPlaced("b"),
            new DeliveryOptions { Headers = { [NatsMsgIdHeader] = fixedMsgId } });

        (await CountStreamMessagesAsync(natsUrl, stream)).ShouldBe(1);
    }

    [Fact]
    public async Task distinct_messages_are_not_deduplicated_with_default_envelope_id()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var stream = $"DEDUP_{Guid.NewGuid():N}";
        var subject = $"dedup.default.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DedupDefault";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s
                        .WithSubjects(subject)
                        .WithDeduplicationWindow(5.Minutes()));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).SendInline();
            })
            .StartAsync();

        var bus = host.MessageBus();

        // Default Nats-Msg-Id is the (unique) envelope Id, so two distinct sends must both persist —
        // guards against over-eager dedup collapsing unrelated messages.
        await bus.SendAsync(new OrderPlaced("a"));
        await bus.SendAsync(new OrderPlaced("b"));

        (await CountStreamMessagesAsync(natsUrl, stream)).ShouldBe(2);
    }

    // Query the stream state over an independent connection (no dependency on Wolverine internals): the
    // publish-acks have already been awaited, so the count is authoritative.
    private static async Task<long> CountStreamMessagesAsync(string natsUrl, string streamName)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = natsUrl });
        await connection.ConnectAsync();

        var js = connection.CreateJetStreamContext();
        var stream = await js.GetStreamAsync(streamName);
        return stream.Info.State.Messages;
    }
}
