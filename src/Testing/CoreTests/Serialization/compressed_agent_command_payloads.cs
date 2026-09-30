using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Serialization;

/// <summary>
/// GH-4720. Agent command payloads are lists of agent URIs, and a list of thousands of sibling agents is
/// nearly all repeated structure. Compressing them shrinks the control queue rows a large cluster writes and
/// re-reads on every poll, and raises the ceiling under MaxIncomingEnvelopeDataSize that GH-4718 ran into.
///
/// The format is selected by CONTENT TYPE rather than by sniffing the payload, so a node that does not know
/// the content type fails to resolve a serializer by name instead of handing gzip bytes to a UTF-8 reader.
/// </summary>
public class compressed_agent_command_payloads
{
    private static Uri[] manyAgents(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Uri(
                $"event-subscriptions://marten/OrderSummaryProjection@tenant-{i:00000}" +
                $"/database/prod-eu-west-1-shard-{i % 512:000}.customer-data.internal" +
                $"/schema/wolverine_events_partitioned/subscription/rolling-window/shard-{i % 950:000}"))
            .ToArray();
    }

    private static Envelope envelopeFor(object message)
    {
        return new Envelope
        {
            Message = message,
            Serializer = CompressedIntrinsicSerializer.Instance,
            ContentType = CompressedIntrinsicSerializer.MimeType
        };
    }

    [Fact]
    public void a_large_agent_list_round_trips()
    {
        var command = new StartAgents(manyAgents(2000));
        var envelope = envelopeFor(command);

        envelope.Data = CompressedIntrinsicSerializer.Instance.Write(envelope);

        var read = (StartAgents)CompressedIntrinsicSerializer.Instance
            .ReadFromData(typeof(StartAgents), envelope);

        read.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void a_large_agent_list_is_actually_smaller_on_the_wire()
    {
        var command = new StartAgents(manyAgents(2000));
        var envelope = envelopeFor(command);

        var compressed = CompressedIntrinsicSerializer.Instance.Write(envelope);
        var plain = IntrinsicSerializer.Instance.Write(envelope);

        // The whole point. A modest floor rather than a precise ratio -- the measured range across realistic
        // URI shapes was 3.9x (GUID-dominated) to 24x (structural, sorted), and a tight assertion here would
        // be measuring this test's URI generator instead of the behaviour.
        compressed.Length.ShouldBeLessThan(plain.Length / 2);
    }

    [Fact]
    public void a_small_payload_travels_uncompressed_rather_than_growing()
    {
        // gzip has ~20 bytes of fixed overhead, so compressing a two-agent command would make it bigger.
        var command = new StartAgents([new Uri("fake://one"), new Uri("fake://two")]);
        var envelope = envelopeFor(command);

        var compressed = CompressedIntrinsicSerializer.Instance.Write(envelope);
        var plain = IntrinsicSerializer.Instance.Write(envelope);

        // Exactly the framing byte, and nothing else
        compressed.Length.ShouldBe(plain.Length + 1);
    }

    [Fact]
    public void a_small_payload_still_round_trips()
    {
        var command = new StartAgents([new Uri("fake://one"), new Uri("fake://two")]);
        var envelope = envelopeFor(command);
        envelope.Data = CompressedIntrinsicSerializer.Instance.Write(envelope);

        ((StartAgents)CompressedIntrinsicSerializer.Instance.ReadFromData(typeof(StartAgents), envelope))
            .AgentUris.Length.ShouldBe(2);
    }

    [Fact]
    public void incompressible_data_falls_back_rather_than_growing()
    {
        // Random bytes gzip larger than they started. Whatever the payload, turning the option on must never
        // make it worse than leaving it off.
        var raw = new byte[4096];
        Random.Shared.NextBytes(raw);

        var packed = CompressedIntrinsicSerializer.Pack(raw);

        packed.Length.ShouldBe(raw.Length + 1);
        CompressedIntrinsicSerializer.Unpack(packed).ShouldBe(raw);
    }

    [Fact]
    public void an_unknown_framing_marker_is_reported_rather_than_misread()
    {
        var bogus = new byte[] { 42, 1, 2, 3 };

        Should.Throw<WolverineSerializationException>(() => CompressedIntrinsicSerializer.Unpack(bogus))
            .Message.ShouldContain("framing marker 42");
    }

    [Fact]
    public void the_compressed_form_has_its_own_content_type()
    {
        // The version marker. A node that does not know this content type fails to find a serializer for it
        // by name, rather than reading gzip bytes as UTF-8 and producing nonsense.
        CompressedIntrinsicSerializer.MimeType.ShouldNotBe(IntrinsicSerializer.MimeType);
    }

    [Fact]
    public async Task the_compressed_serializer_is_always_readable_even_when_writing_is_off()
    {
        // The rollout contract: reading works on every node from this version forward, so a fleet can be
        // fully deployed before anyone turns writing on.
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync(TestContext.Current.CancellationToken);

        var options = host.GetRuntime().Options;
        options.Durability.CompressAgentCommands.ShouldBeFalse("Default must stay off for rolling deploys");

        options.TryFindSerializer(CompressedIntrinsicSerializer.MimeType).ShouldBeSameAs(
            CompressedIntrinsicSerializer.Instance);
    }

    private static async Task<IMessageSerializer?> serializerForAgentCommand(bool compress)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Durability.CompressAgentCommands = compress)
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(new Uri("local://" + TransportConstants.Agents))!;

        return new MessageRoute(typeof(StartAgents), endpoint, runtime).Serializer;
    }

    [Fact]
    public async Task the_option_selects_the_compressed_serializer_for_an_agent_command()
    {
        // The wiring, not just the serializer. Without this the whole thing is an unreachable class.
        (await serializerForAgentCommand(true)).ShouldBeSameAs(CompressedIntrinsicSerializer.Instance);
    }

    [Fact]
    public async Task the_default_leaves_agent_commands_on_the_plain_serializer()
    {
        var serializer = await serializerForAgentCommand(false);

        serializer.ShouldNotBeSameAs(CompressedIntrinsicSerializer.Instance);
        serializer!.ContentType.ShouldBe(IntrinsicSerializer.MimeType);
    }
}
