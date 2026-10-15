using IntegrationTests;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PostgresqlTests.Agents;

/// <summary>
///     GH-4569. A <see cref="NodeRole.Warmup" /> node joins the serving cluster to warm up the new release's projections,
///     and must do none of the serving nodes' messaging. On 10/8 a green node in that position picked up production
///     work from its listeners, from inbox recovery and from scheduled rows, appended an event type only the new
///     release knows, and paused every async projection of those tenants on the serving version.
///
///     Each fact starts a serving host and a warm-up host from the same configuration, so what the warm-up host is
///     missing is missing because of its role, not because the configuration never asked for it.
/// </summary>
public class warmup_node_role_host_4569 : IAsyncLifetime
{
    private const string SchemaName = "warmup_role_4569";

    private IHost _serving = null!;
    private IHost _warmup = null!;

    private static Task<IHost> startAsync(NodeRole role)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "warmup-4569";
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.NodeRole = role;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName)
                    .AutoProvision();

                // An application listener the serving node starts, and a warm-up node must not
                opts.ListenToPostgresqlQueue("intake");

                // Balanced startup reads wolverine_nodes, so the schema has to exist before the runtime starts
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask InitializeAsync()
    {
        _serving = await startAsync(NodeRole.Serving);
        _warmup = await startAsync(NodeRole.Warmup);
    }

    public async ValueTask DisposeAsync()
    {
        await _warmup.StopAsync();
        _warmup.Dispose();

        await _serving.StopAsync();
        _serving.Dispose();
    }

    [Fact]
    public void the_warmup_node_listens_only_to_its_control_endpoint()
    {
        var serving = _serving.GetRuntime();
        serving.Endpoints.ActiveListeners().ShouldContain(x => x.Uri.Scheme == PostgresqlTransport.ProtocolName);

        var warmup = _warmup.GetRuntime();
        var listening = warmup.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();

        listening.ShouldNotContain(x => x.Scheme == PostgresqlTransport.ProtocolName);
        listening.ShouldBe([warmup.Options.Transports.NodeControlEndpoint!.Uri]);
    }

    [Fact]
    public void the_warmup_node_does_not_poll_scheduled_messages()
    {
        _serving.GetRuntime().As<WolverineRuntime>().DurableScheduledJobs.ShouldNotBeNull();
        _warmup.GetRuntime().As<WolverineRuntime>().DurableScheduledJobs.ShouldBeNull();
    }

    [Fact]
    public async Task the_warmup_node_publishes_its_role_instead_of_the_durability_marker()
    {
        var nodes = await _serving.GetRuntime().Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);

        var serving = nodes.Single(x => x.NodeId == _serving.GetRuntime().Options.UniqueNodeId);
        serving.Capabilities.ShouldContain(MessageStoreCollection.DurabilityCapabilityUri);
        serving.Capabilities.ShouldNotContain(NodeAgentController.WarmupRoleUri);

        var warmup = nodes.Single(x => x.NodeId == _warmup.GetRuntime().Options.UniqueNodeId);
        warmup.Capabilities.ShouldContain(NodeAgentController.WarmupRoleUri);
        warmup.Capabilities.ShouldNotContain(MessageStoreCollection.DurabilityCapabilityUri);
    }

    [Fact]
    public void the_warmup_node_can_never_lead()
    {
        _serving.GetRuntime().Options.Durability.CanLead.ShouldBeTrue();
        _warmup.GetRuntime().Options.Durability.CanLead.ShouldBeFalse();
    }
}
