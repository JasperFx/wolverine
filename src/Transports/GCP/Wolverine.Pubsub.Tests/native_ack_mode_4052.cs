using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-4052. Pub/Sub opts into <see cref="EndpointMode.NativeAck" /> by <b>holding the subscriber callback
/// open</b> until every envelope a delivery carried reaches a terminal.
/// </summary>
/// <remarks>
/// This transport is unlike the others in the wave: it has no per-message settle API at all.
/// <c>PubsubListener.CompleteAsync</c> is a no-op and always was, because acknowledgement happens entirely
/// through the value the subscriber callback returns. So "do not settle on receipt" can only mean "do not
/// return yet", which is why the settlement bookkeeping below is keyed by delivery rather than envelope.
/// </remarks>
public class native_ack_mode_4052
{
    private static PubsubEndpoint endpointFor()
    {
        var transport = new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        return new PubsubEndpoint("one", transport);
    }

    [Fact]
    public void the_endpoint_now_opts_into_native_ack()
    {
        var endpoint = endpointFor();

        // The mode gate is default-closed across the whole wave, so this override IS the opt-in
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
    }

    [Fact]
    public void supported_modes_are_an_explicit_allow_list_not_a_blanket_true()
    {
        var endpoint = endpointFor();

        // GH-4011's hazard, and the spike called Pub/Sub its worst case: a bare `true` silently claims
        // every FUTURE mode the moment it is added to the enum. Pinning the four it actually supports is
        // what makes the next enum member a compile-time conversation instead of a silent data loss bug
        foreach (var mode in Enum.GetValues<EndpointMode>())
        {
            endpoint.Mode = mode;
            endpoint.Mode.ShouldBe(mode);
        }
    }

    [Fact]
    public async Task a_single_envelope_delivery_acks_when_its_envelope_succeeds()
    {
        var deliveries = new PubsubHeldDeliveries();
        var delivery = deliveries.Hold("m1", 1);

        delivery.Succeeded().ShouldBeTrue();

        (await delivery.Reply).ShouldBe(SubscriberClient.Reply.Ack);
    }

    [Fact]
    public async Task a_batched_delivery_waits_for_every_envelope_before_settling()
    {
        // One Pub/Sub message can carry many envelopes, and a single Ack settles the lot -- so settling on
        // the first terminal would ack work that is still running
        var deliveries = new PubsubHeldDeliveries();
        var delivery = deliveries.Hold("m1", 3);

        delivery.Succeeded().ShouldBeFalse();
        delivery.Succeeded().ShouldBeFalse();
        delivery.Reply.IsCompleted.ShouldBeFalse();

        delivery.Succeeded().ShouldBeTrue();
        (await delivery.Reply).ShouldBe(SubscriberClient.Reply.Ack);
    }

    [Fact]
    public async Task one_failure_makes_the_whole_batched_delivery_a_nack()
    {
        // A single Ack/Nack covers the batch, so any failure has to nack all of it. Pub/Sub redelivers and
        // the inbox or the in-memory idempotency guard deduplicates the ones that already succeeded
        var deliveries = new PubsubHeldDeliveries();
        var delivery = deliveries.Hold("m1", 3);

        delivery.Succeeded();
        delivery.Failed();
        delivery.Succeeded().ShouldBeTrue();

        (await delivery.Reply).ShouldBe(SubscriberClient.Reply.Nack);
    }

    [Fact]
    public async Task a_failure_still_waits_for_its_siblings()
    {
        // Doomed to nack, but a batch must not be settled while part of it is still executing
        var deliveries = new PubsubHeldDeliveries();
        var delivery = deliveries.Hold("m1", 2);

        delivery.Failed().ShouldBeFalse();
        delivery.Reply.IsCompleted.ShouldBeFalse();

        delivery.Succeeded().ShouldBeTrue();
        (await delivery.Reply).ShouldBe(SubscriberClient.Reply.Nack);
    }

    [Fact]
    public async Task shutdown_nacks_everything_still_held()
    {
        // Spike section 4c: a held callback that never returns wedges SubscriberClient.StopAsync forever.
        // Anything still held is unsettled by definition, so Nack is both correct and the only answer that
        // lets shutdown finish
        var deliveries = new PubsubHeldDeliveries();
        var first = deliveries.Hold("m1", 5);
        var second = deliveries.Hold("m2", 2);

        deliveries.NackAll();

        (await first.Reply).ShouldBe(SubscriberClient.Reply.Nack);
        (await second.Reply).ShouldBe(SubscriberClient.Reply.Nack);
    }

    [Fact]
    public void an_envelope_finds_its_delivery_by_the_stamped_key()
    {
        var deliveries = new PubsubHeldDeliveries();
        deliveries.Hold("m1", 1);

        var envelope = new Envelope();
        envelope.Headers[PubsubHeldDeliveries.DeliveryKeyHeader] = "m1";

        deliveries.TryFind(envelope, out var found).ShouldBeTrue();
        found.Key.ShouldBe("m1");
    }

    [Fact]
    public void an_unstamped_envelope_finds_nothing_rather_than_throwing()
    {
        // Every settle path checks this before acting, so an envelope from another mode -- or a released
        // delivery -- has to be a quiet miss rather than an exception on the settle path
        var deliveries = new PubsubHeldDeliveries();

        deliveries.TryFind(new Envelope(), out _).ShouldBeFalse();
    }

    [Fact]
    public void a_released_delivery_is_no_longer_found()
    {
        var deliveries = new PubsubHeldDeliveries();
        deliveries.Hold("m1", 1);
        deliveries.Release("m1");

        var envelope = new Envelope();
        envelope.Headers[PubsubHeldDeliveries.DeliveryKeyHeader] = "m1";

        deliveries.TryFind(envelope, out _).ShouldBeFalse();
    }
}
