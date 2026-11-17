using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Agents;

/// <summary>
/// GH-4720. The unit facts prove the serializer; this proves the wire. A compressed agent command has to
/// survive a real crossing between two processes — serialized by one node, written to the control queue
/// table, read back by another, and dispatched to a serializer chosen from the CONTENT TYPE carried in the
/// envelope header rather than by sniffing the payload.
///
/// That is also the whole reason compression is a separate content type: a node that has never heard of
/// binary/wolverine+gzip fails to resolve a serializer by name, loudly, instead of handing gzip bytes to a
/// UTF-8 URI parser and producing nonsense.
/// </summary>
public class compressed_agent_commands_over_the_control_queue : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "pgcompressed";
    private const int AgentCount = 2000;

    private IHost _sender = null!;
    private IHost _receiver = null!;
    private Uri _receiverUri = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        _sender = await startHost("CompressedSender");
        _receiver = await startHost("CompressedReceiver");

        _receiverUri = new Uri($"dbcontrol://{_receiver.GetRuntime().Options.UniqueNodeId}");
    }

    private static Task<IHost> startHost(string serviceName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.ServiceName = serviceName;

                // THIS is the thing under test
                opts.Durability.CompressAgentCommands = true;

                // A family of 2,000 agents this node can actually start, so the reply names back exactly
                // what it recognised out of the payload.
                opts.Services.AddSingleton<IAgentFamily>(new FakeAgentFamily("fake", AgentCount));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    private static Uri[] manyAgents(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new Uri(
                $"event-subscriptions://marten/OrderSummaryProjection@tenant-{i:00000}" +
                $"/database/prod-eu-west-1-shard-{i % 512:000}.customer-data.internal" +
                $"/schema/wolverine_events_partitioned/subscription/rolling-window/shard-{i % 950:000}"))
            .ToArray();
    }

    [Fact]
    public async Task a_large_compressed_agent_command_crosses_the_control_queue()
    {
        var receiverRuntime = _receiver.GetRuntime();
        var agents = new FakeAgentFamily("fake", AgentCount).AllAgentUris();

        var destination = new NodeDestination(receiverRuntime.Options.UniqueNodeId, _receiverUri);

        // The real agent control path, at the shape and scale that caused GH-4718: StartAgents is serialized
        // here, gzipped under binary/wolverine+gzip, written to the control queue table, read back by the
        // other host, dispatched to a serializer resolved from the envelope's CONTENT TYPE, executed, and
        // answered.
        //
        // The reply names back every agent the receiver actually started, which is what makes this sensitive
        // to the payload rather than merely to the round trip completing: a list that arrives truncated or
        // mis-decompressed starts a different set, and this fails.
        var started = await _sender.GetRuntime().Agents
            .InvokeAsync<AgentsStarted>(destination, new StartAgents(agents), 120.Seconds());

        started.ShouldNotBeNull();
        started.AgentUris.Length.ShouldBe(AgentCount);
        started.AgentUris.OrderBy(x => x.ToString()).ShouldBe(agents.OrderBy(x => x.ToString()));
    }
}

/// <summary>
/// GH-5200. CompressAgentCommands is on by default in 7.0. A cluster mid-upgrade has nodes that write compressed
/// agent commands and nodes that do not, and both have to read each other: the compressed content type has been
/// readable since 6.44 whatever the setting says. Here one node runs the 7.0 default and the other has the 6.x
/// defaults restored, so it writes the plain format and stands in for a 6.44+ node. Commands cross the real
/// control queue in both directions. A node older than 6.44 cannot read the compressed content type at all,
/// which is the documented floor: every node on 6.44 or later before the first 7.0 node starts.
/// </summary>
public class mixed_agent_command_compression_over_the_control_queue : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "pgmixedcompression";
    private const int AgentCount = 500;

    private IHost _compressing = null!;
    private IHost _plain = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        _compressing = await startHost("MixedCompressing", restoreV6Defaults: false);
        _plain = await startHost("MixedPlain", restoreV6Defaults: true);
    }

    private static Task<IHost> startHost(string serviceName, bool restoreV6Defaults)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (restoreV6Defaults) opts.RestoreV6Defaults();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.ServiceName = serviceName;
                opts.Services.AddSingleton<IAgentFamily>(new FakeAgentFamily("fake", AgentCount));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _compressing.StopAsync();
        _compressing.Dispose();
        await _plain.StopAsync();
        _plain.Dispose();
    }

    private static async Task startAgentsOn(IHost sender, IHost receiver)
    {
        var receiverRuntime = receiver.GetRuntime();
        var agents = new FakeAgentFamily("fake", AgentCount).AllAgentUris();
        var destination = new NodeDestination(receiverRuntime.Options.UniqueNodeId,
            new Uri($"dbcontrol://{receiverRuntime.Options.UniqueNodeId}"));

        var started = await sender.GetRuntime().Agents
            .InvokeAsync<AgentsStarted>(destination, new StartAgents(agents), 120.Seconds());

        started.ShouldNotBeNull();
        started.AgentUris.OrderBy(x => x.ToString()).ShouldBe(agents.OrderBy(x => x.ToString()));
    }

    [Fact]
    public void the_settings_differ_as_intended()
    {
        _compressing.GetRuntime().Options.Durability.CompressAgentCommands.ShouldBeTrue();
        _plain.GetRuntime().Options.Durability.CompressAgentCommands.ShouldBeFalse();
    }

    [Fact]
    public async Task a_node_writing_the_plain_format_reads_a_compressed_command()
    {
        await startAgentsOn(_compressing, _plain);
    }

    [Fact]
    public async Task a_compressing_node_reads_a_plain_command()
    {
        await startAgentsOn(_plain, _compressing);
    }
}
