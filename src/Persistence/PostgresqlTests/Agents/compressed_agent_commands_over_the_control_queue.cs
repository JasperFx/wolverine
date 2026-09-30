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
