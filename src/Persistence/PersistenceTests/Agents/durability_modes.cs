using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace PersistenceTests.Agents;

public class durability_modes : PostgresqlContext, IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public durability_modes(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void default_mode_is_balanced()
    {
        new DurabilitySettings().Mode.ShouldBe(DurabilityMode.Balanced);
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    private async Task<WolverineRuntime> withConfig(DurabilityMode mode, Action<WolverineOptions>? configure = null)
    {
        // clear out garbage nodes
        await dropSchema();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddResourceSetupOnStartup();

                opts.Durability.Mode = mode;

                configure?.Invoke(opts);
            }).StartAsync();

        new XUnitEventObserver(_host, _output);

        return _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    protected async Task stopAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    [Fact]
    public async Task start_in_balanced_mode()
    {
        var runtime = await withConfig(DurabilityMode.Balanced);


        runtime.NodeController.ShouldNotBeNull();
        runtime.NodeController.HasStartedLocalAgentWorkflowForBalancedMode.ShouldBeTrue();
        runtime.NodeController.HasStartedInSoloMode.ShouldBeFalse();

        // Should be listening on the control endpoint
        runtime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == runtime.Options.Transports.NodeControlEndpoint.Uri)
            .ShouldBeTrue();

        // Should start up the durable scheduled jobs
        runtime.DurableScheduledJobs.ShouldNotBeNull();
        runtime.ScheduledJobs.ShouldNotBeNull();

        // verify that there's a persisted node
        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldNotBeNull();

        // Should be leader and have all agents running
        var tracker = runtime.Tracker;

        // All agents should be running here
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_host, 12);
        }, 30.Seconds());

        await _host.WaitUntilAssumesLeadershipAsync(30.Seconds());

        // Deletes the current node on stop
        await stopAsync();
        (await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task start_in_solo_mode()
    {
        var runtime = await withConfig(DurabilityMode.Solo);

        runtime.NodeController.ShouldNotBeNull();
        runtime.NodeController.HasStartedLocalAgentWorkflowForBalancedMode.ShouldBeFalse();
        runtime.NodeController.HasStartedInSoloMode.ShouldBeTrue();

        // Should NOT be listening on the control endpoint
        runtime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == runtime.Options.Transports.NodeControlEndpoint.Uri)
            .ShouldBeFalse();

        // Should start up the durable scheduled jobs
        runtime.DurableScheduledJobs.ShouldNotBeNull();
        runtime.ScheduledJobs.ShouldNotBeNull();

        // verify that there's NOT a persisted node
        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldBeNull();

        // All agents should be running here
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_host, 12);
        }, 30.Seconds());

        // Deletes the current node on stop
        await stopAsync();
        (await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task start_in_serverless_mode()
    {
        var runtime = await withConfig(DurabilityMode.Serverless);

        runtime.NodeController.ShouldBeNull();

        // Should NOT be listening on the control endpoint
        runtime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == runtime.Options.Transports.NodeControlEndpoint.Uri)
            .ShouldBeFalse();

        // Removes all local endpoints
        runtime.Endpoints.ActiveSendingAgents().Any(x => x.Destination.Scheme == TransportConstants.Local)
            .ShouldBeFalse();

        // Should NOT start up the durable scheduled jobs
        runtime.DurableScheduledJobs.ShouldBeNull();
        runtime.ScheduledJobs.ShouldBeNull();

        // verify that there's no persisted node
        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldBeNull();
    }

    [Fact]
    public async Task start_in_mediator_mode()
    {
        var runtime = await withConfig(DurabilityMode.MediatorOnly);

        runtime.NodeController.ShouldBeNull();

        // Should NOT be listening on the control endpoint
        runtime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == runtime.Options.Transports.NodeControlEndpoint.Uri)
            .ShouldBeFalse();

        // Should NOT start up the durable scheduled jobs
        runtime.DurableScheduledJobs.ShouldBeNull();
        runtime.ScheduledJobs.ShouldBeNull();

        // verify that there's no persisted node
        var node = await runtime.Storage.Nodes.LoadNodeAsync(runtime.Options.UniqueNodeId, CancellationToken.None);
        node.ShouldBeNull();
    }

    [Fact]
    public async Task serverless_mode_asserts_on_any_non_inline_endpoints()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await withConfig(DurabilityMode.Serverless, o => o.PublishAllMessages().ToPort(PortFinder.GetAvailablePort()));
        });
    }

    [Fact]
    public async Task mediator_does_not_start_any_transports()
    {
        var runtime = await withConfig(DurabilityMode.MediatorOnly, o => o.ListenAtPort(PortFinder.GetAvailablePort()));
        runtime.Endpoints.ActiveListeners().Any().ShouldBeFalse();

        // Okay to have local queues
        var agents = runtime.Endpoints.ActiveSendingAgents().ToArray();
        agents.Any().ShouldBeFalse();
    }
}