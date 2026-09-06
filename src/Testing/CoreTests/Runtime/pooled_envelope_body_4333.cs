using System.Buffers;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Runtime;

public record PooledBodyMessage(string Name);

/// <summary>
/// GH-4333. A large payload is copied into a pooled array on the receive path instead of a fresh
/// large-object-heap allocation. The copy was never optional -- a broker client's buffer dies with
/// its callback -- so what changes is where the bytes land and who owns them.
///
/// <para>
/// The rules that make this safe are all about lifetime, so they are what this file tests:
/// <c>Data</c> hands back an INDEPENDENT array that survives the buffer going home, <c>Body</c> does
/// not copy, and the buffer is only ever returned at <c>Reset</c>. Never returning is a missed
/// optimization; returning early hands a live buffer to the next renter, so every rule here is
/// biased towards holding on.
/// </para>
/// </summary>
public class pooled_envelope_body_4333
{
    private static byte[] payload(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static readonly int Small = Envelope.PooledBodyThreshold - 1;
    private static readonly int Large = Envelope.PooledBodyThreshold;

    [Fact]
    public void a_small_payload_is_not_pooled_and_behaves_exactly_as_before()
    {
        var bytes = payload(Small);
        var envelope = new Envelope();

        envelope.CopyBodyFrom(bytes);

        // Below the gate the envelope holds a plain array, which is the shape every existing caller
        // has always seen -- Data does not have to materialize anything
        envelope.Data.ShouldBe(bytes);
        envelope.Body.ToArray().ShouldBe(bytes);
        envelope.Data.ShouldBeSameAs(envelope.Data);
    }

    [Fact]
    public void a_large_payload_round_trips_through_the_pooled_path()
    {
        var bytes = payload(Large);
        var envelope = new Envelope();

        envelope.CopyBodyFrom(bytes);

        envelope.Body.Length.ShouldBe(bytes.Length);
        envelope.Body.ToArray().ShouldBe(bytes);
        envelope.Data.ShouldBe(bytes);
    }

    /// <summary>
    /// The whole point of the pooled path: reading the body does not copy it.
    /// </summary>
    [Fact]
    public void body_does_not_copy()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        var first = envelope.Body;
        var second = envelope.Body;

        first.Span.Overlaps(second.Span).ShouldBeTrue("Body handed back two different buffers");
    }

    /// <summary>
    /// The safety rule that lets the buffer go back at all. A caller who reads Data is allowed to
    /// stash the result, so it must not be an array the pool can hand to somebody else.
    /// </summary>
    [Fact]
    public void data_materializes_an_array_that_outlives_the_pooled_buffer()
    {
        var bytes = payload(Large);
        var envelope = new Envelope();
        envelope.CopyBodyFrom(bytes);

        var stashed = envelope.Data!;

        // The runtime decides this envelope is finished; the rental goes home
        envelope.Reset();

        stashed.ShouldBe(bytes, "the array handed out by Data was the pooled buffer, not a copy of it");
    }

    [Fact]
    public void data_materializes_only_once()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        envelope.Data.ShouldBeSameAs(envelope.Data);
    }

    [Fact]
    public void reset_clears_the_body()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        envelope.Reset();

        // Deliberately not asserting on Body here: after Reset there is no payload AND no message, and
        // Body falls through to Data, which serializes on demand and therefore throws when there is
        // nothing to serialize -- exactly as Data has always done. MessagePayloadSize answers the
        // question this test is actually asking without tripping that path.
        envelope.MessagePayloadSize.ShouldBeNull();
    }

    /// <summary>
    /// Body is NOT a plain field read: with no payload yet it falls through to Data, which serializes
    /// the message on demand. That behaviour is load-bearing -- EnvelopeSerializer writes from Body, so
    /// if Body reported "empty" for an unserialized message every persisted envelope would carry an
    /// empty payload.
    /// </summary>
    [Fact]
    public void body_serializes_an_unserialized_message_on_demand_exactly_as_data_does()
    {
        var envelope = new Envelope(new PooledBodyMessage("hello"))
        {
            Serializer = new SystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions())
        };

        var fromBody = envelope.Body.ToArray();

        fromBody.ShouldNotBeEmpty();
        fromBody.ShouldBe(envelope.Data!);
    }

    /// <summary>
    /// Reset returns the rental, so calling it twice must not return the same buffer twice -- a
    /// double-return corrupts the pool for every later renter.
    /// </summary>
    [Fact]
    public void reset_is_idempotent()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        envelope.Reset();
        Should.NotThrow(() => envelope.Reset());
    }

    /// <summary>
    /// Assigning Data replaces the payload outright, so the rental it replaces has to go back rather
    /// than be silently dropped.
    /// </summary>
    [Fact]
    public void assigning_data_over_a_pooled_body_releases_the_rental()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        var replacement = payload(16);
        envelope.Data = replacement;

        envelope.Data.ShouldBeSameAs(replacement);
        envelope.Body.ToArray().ShouldBe(replacement);
        Should.NotThrow(() => envelope.Reset());
    }

    [Fact]
    public void copying_a_second_body_releases_the_first_rental()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        var second = payload(Large);
        envelope.CopyBodyFrom(second);

        envelope.Body.ToArray().ShouldBe(second);
        Should.NotThrow(() => envelope.Reset());
    }

    [Fact]
    public void message_payload_size_does_not_force_a_materialization()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(payload(Large));

        envelope.MessagePayloadSize.ShouldBe(Large);
    }

    /// <summary>
    /// The persisted-envelope path reads the body rather than Data, so a pooled payload survives a
    /// durable round trip without ever being materialized on the way out.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void envelope_serializer_round_trips_a_pooled_body(bool large)
    {
        var bytes = payload(large ? Large : Small);

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "some.message",
            ContentType = "application/json",
            Destination = new Uri("rabbitmq://queue/incoming")
        };
        envelope.CopyBodyFrom(bytes);

        var serialized = EnvelopeSerializer.Serialize(envelope);
        var read = EnvelopeSerializer.Deserialize(serialized);

        read.Data.ShouldBe(bytes);
    }
}
