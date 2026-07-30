using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class agent_command_serialization
{
    protected T roundTrip<T>(T command) where T : IAgentCommand, ISerializable
    {
        var serializer = new IntrinsicSerializer<T>();
        var bytes = serializer.Write(new Envelope(command));
        return (T)serializer.ReadFromData(bytes);
    }

    [Fact]
    public void StartAgents()
    {
        var command = new StartAgents([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void StartAgents_with_no_agents()
    {
        var other = roundTrip(new StartAgents([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void AgentsStarted()
    {
        var command = new AgentsStarted([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void AgentsStarted_with_no_agents()
    {
        // GH-3460 -- StartAgents can legitimately report zero successfully
        // started agents during node churn
        var other = roundTrip(new AgentsStarted([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void StopAgents()
    {
        var command = new StopAgents([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void StopAgents_with_no_agents()
    {
        var other = roundTrip(new StopAgents([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void AgentsStopped()
    {
        var command = new AgentsStopped([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void AgentsStopped_with_no_agents()
    {
        var other = roundTrip(new AgentsStopped([]));

        other.AgentUris.ShouldBeEmpty();
    }

    // GH-3733: a comma is a legal URI sub-delimiter (RFC 3986), permitted unescaped in a path segment, and
    // agent URIs embed operator- and tenant-supplied strings -- an event-subscription URI carries the
    // projection name and the tenant id. Joining on a comma shattered any such agent into fragments, and
    // because the read side built the array in one LINQ projection the resulting UriFormatException took out
    // the confirmation for the ENTIRE batch. The third case below is the one that mattered in production:
    // two agents, one with a comma, and neither confirmed.
    public static IEnumerable<object[]> CommaBearingUris()
    {
        yield return [new[] { new Uri("event-subscriptions://marten/main/db/proj/all/v1/a,b") }];
        yield return [new[] { new Uri("event-subscriptions://marten/main/db/proj/all/v1/a, b") }];
        yield return
        [
            new[]
            {
                new Uri("event-subscriptions://marten/main/db/proj/all/v1/plain"),
                new Uri("event-subscriptions://marten/main/db/proj/all/v1/a,b")
            }
        ];
    }

    [Theory]
    [MemberData(nameof(CommaBearingUris))]
    public void round_trip_agent_uris_containing_a_comma(Uri[] uris)
    {
        roundTrip(new StartAgents(uris)).AgentUris.ShouldBe(uris);
        roundTrip(new AgentsStarted(uris)).AgentUris.ShouldBe(uris);
        roundTrip(new StopAgents(uris)).AgentUris.ShouldBe(uris);
        roundTrip(new AgentsStopped(uris)).AgentUris.ShouldBe(uris);
    }

    // The comma stays the default on the wire so a rolling upgrade keeps working in both directions: a
    // 6.24.0 node has to be able to read what a 6.24.1 node writes, and vice versa. Only a payload that
    // actually contains a comma -- the case that was already broken on both -- switches format.
    [Fact]
    public void payloads_without_a_comma_keep_the_legacy_wire_format()
    {
        var command = new StartAgents([new Uri("fake://one"), new Uri("fake://two")]);

        System.Text.Encoding.UTF8.GetString(command.Write())
            .ShouldBe("fake://one/,fake://two/");
    }

    [Fact]
    public void reads_a_legacy_comma_joined_payload()
    {
        var legacy = System.Text.Encoding.UTF8.GetBytes("fake://one,fake://two,fake://three");

        ((Wolverine.Runtime.Agents.StartAgents)Wolverine.Runtime.Agents.StartAgents.Read(legacy)).AgentUris
            .ShouldBe([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);
    }

    [Fact]
    public void an_unreadable_entry_names_itself_and_the_payload()
    {
        var garbage = System.Text.Encoding.UTF8.GetBytes("fake://one,%%%not a uri");

        var ex = Should.Throw<FormatException>(() => Wolverine.Runtime.Agents.StartAgents.Read(garbage));
        ex.Message.ShouldContain("%%%not a uri");
        ex.Message.ShouldContain("fake://one,%%%not a uri");
    }
}