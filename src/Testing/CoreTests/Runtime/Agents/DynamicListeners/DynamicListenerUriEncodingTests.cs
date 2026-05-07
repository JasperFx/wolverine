using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents.DynamicListeners;

/// <summary>
/// Unit coverage for <see cref="DynamicListenerUriEncoding"/> — the encoding
/// has to be lossless and idempotent for cluster assignment to work, since
/// every cycle the cluster compares the agent URIs returned by
/// <c>AllKnownAgentsAsync</c> against the previous cycle's set. Any encoding
/// jitter (different escape rules for the same input) would manifest as
/// "agents thrashing on/off" between assignment polls.
/// </summary>
public class DynamicListenerUriEncodingTests
{
    [Theory]
    [InlineData("mqtt://broker:1883/devices/foo/status")]
    [InlineData("mqtt://localhost/iot/+/temp")] // MQTT topic wildcards
    [InlineData("rabbitmq://localhost:5672/queue.name")]
    [InlineData("kafka://broker.local:9092/orders")]
    [InlineData("amqp://user:pass@host:5672/vhost/queue")] // userinfo, port, multi-segment path
    [InlineData("mqtt://broker/path?qos=1")] // query string
    public void encoding_round_trips_listener_uri(string raw)
    {
        var listener = new Uri(raw);

        var agentUri = DynamicListenerUriEncoding.ToAgentUri(listener);
        var decoded = DynamicListenerUriEncoding.ToListenerUri(agentUri);

        decoded.ShouldBe(listener);
    }

    [Fact]
    public void agent_uri_uses_dynamic_listener_scheme()
    {
        var agentUri = DynamicListenerUriEncoding.ToAgentUri(new Uri("mqtt://broker/topic"));
        agentUri.Scheme.ShouldBe(DynamicListenerUriEncoding.SchemeName);
    }

    [Fact]
    public void encoding_is_deterministic_for_the_same_input()
    {
        // The cluster's assignment grid keys agents by their URI string —
        // the same listener URI must always produce the same agent URI so
        // an agent that was running before a poll cycle is recognized as the
        // same agent after the poll.
        var listener = new Uri("mqtt://broker/devices/foo/status");

        var first = DynamicListenerUriEncoding.ToAgentUri(listener);
        var second = DynamicListenerUriEncoding.ToAgentUri(listener);

        second.ShouldBe(first);
    }

    [Fact]
    public void to_listener_uri_rejects_wrong_scheme()
    {
        var bogus = new Uri("wolverine-listener://something");

        Should.Throw<ArgumentException>(() => DynamicListenerUriEncoding.ToListenerUri(bogus))
            .Message.ShouldContain(DynamicListenerUriEncoding.SchemeName);
    }

    [Fact]
    public void to_listener_uri_rejects_empty_path()
    {
        // wolverine-dynamic-listener:/// with nothing after the slashes is not
        // a valid encoded agent URI — guard the decoder so a malformed entry
        // doesn't silently produce a garbage listener URI.
        var emptyPath = new Uri($"{DynamicListenerUriEncoding.SchemeName}:///");

        Should.Throw<ArgumentException>(() => DynamicListenerUriEncoding.ToListenerUri(emptyPath))
            .Message.ShouldContain("no encoded listener URI");
    }
}
