using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Agents;

/// <summary>
///     GH-3954. The end-to-end half of the capability gate: the leader now refuses to assign
///     <c>wolverinedb://</c> agents to a node that has not published
///     <see cref="MessageStoreCollection.DurabilityCapabilityUri" />, so the marker actually reaching
///     <c>WolverineNode.Capabilities</c> at startup is load bearing for every durable Wolverine application.
///     If it ever stops being published, durability agents stop being assigned everywhere — this is the test
///     that says so directly rather than leaving it to an agent count somewhere else.
/// </summary>
public class durability_agent_capability_publication : PostgresqlContext, IAsyncDisposable
{
    private IHost? _host;

    private async Task<WolverineRuntime> startAsync(Action<WolverineOptions>? configure = null)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("durability_capability");
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "durability_capability");
                opts.Services.AddResourceSetupOnStartup();
                opts.Durability.Mode = DurabilityMode.Balanced;

                configure?.Invoke(opts);
            }).StartAsync();

        return _host.GetRuntime();
    }

    private async Task<bool> waitForDurabilityAgent(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (_host!.RunningAgents().Any(x => x.Scheme == "wolverinedb"))
            {
                return true;
            }

            try
            {
                await Task.Delay(250.Milliseconds(), cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        return _host!.RunningAgents().Any(x => x.Scheme == "wolverinedb");
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task a_durability_enabled_node_publishes_the_capability_and_gets_its_agent()
    {
        var runtime = await startAsync();

        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldNotBeNull();
        node.Capabilities.ShouldContain(MessageStoreCollection.DurabilityCapabilityUri);

        await _host!.WaitUntilAssumesLeadershipAsync(30.Seconds());

        // The assignment the gate must NOT block. A regression in publishing the marker shows up here as
        // an empty list rather than as a subtly wrong agent count somewhere else.
        (await waitForDurabilityAgent(TimeSpan.FromSeconds(30)))
            .ShouldBeTrue("the durability agent was never assigned to a node that IS capable of running it");
    }

    [Fact]
    public async Task a_node_with_the_durability_agent_disabled_publishes_nothing_and_is_not_assigned()
    {
        var runtime = await startAsync(opts => opts.Durability.DurabilityAgentEnabled = false);

        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldNotBeNull();
        node.Capabilities.ShouldNotContain(MessageStoreCollection.DurabilityCapabilityUri);

        await _host!.WaitUntilAssumesLeadershipAsync(30.Seconds());

        // Previously the leader assigned anyway, the node threw "Unrecognized agent scheme 'wolverinedb'",
        // and it re-issued the identical assignment every five minutes indefinitely.
        _host!.RunningAgents().ShouldNotContain(x => x.Scheme == "wolverinedb");
    }
}
