using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PostgresqlTests.Agents;

/// <summary>
///     GH-5601. Three nodes of one application, from one configuration that differs only in
///     <c>Durability.Workloads</c>: a node that runs everything, a messaging node (Messaging, Projections) and an
///     HTTP-only node. What a node does not do here, it does not do because of its workloads, not because the
///     configuration never asked for it. The leader's side of the story is in the Bobcat specifications in
///     CoreTests (<c>Runtime/Agents/Workloads</c>).
/// </summary>
public class node_workloads_host_5601 : IAsyncLifetime
{
    private const string SchemaName = "node_workloads_5601";

    private IHost _everything = null!;
    private IHost _messaging = null!;
    private IHost _httpOnly = null!;

    #region sample_node_workloads_from_configuration

    private static Task<IHost> startAsync(NodeWorkloads workloads)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "node-workloads-5601";
                opts.Durability.Mode = DurabilityMode.Balanced;

                // The one line that differs between the three deployments. In a real application this
                // comes from deployment configuration, e.g. Wolverine__Workloads=Messaging,Projections
                opts.Durability.Workloads = workloads;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName)
                    .AutoProvision();

                // An application listener the messaging nodes start, and an HTTP-only node does not
                opts.ListenToPostgresqlQueue("intake");

                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                // Balanced startup reads wolverine_nodes, so the schema has to exist before the runtime starts
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    #endregion

    public async ValueTask InitializeAsync()
    {
        // Started first, so it leads
        _everything = await startAsync(NodeWorkloads.All);
        _messaging = await startAsync(NodeWorkloads.Messaging | NodeWorkloads.Projections);
        _httpOnly = await startAsync(NodeWorkloads.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in new[] { _httpOnly, _messaging, _everything })
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public void the_http_only_node_listens_only_to_its_control_and_reply_endpoints()
    {
        _messaging.GetRuntime().Endpoints.ActiveListeners()
            .ShouldContain(x => x.Uri.Scheme == PostgresqlTransport.ProtocolName && x.Uri.ToString().Contains("intake"));

        var http = _httpOnly.GetRuntime();
        var allowed = EndpointCollection.ControlAndReplyEndpoints(http.Options).Select(x => x.Uri).ToArray();
        var listening = http.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();

        listening.ShouldContain(http.Options.Transports.NodeControlEndpoint!.Uri);
        listening.ShouldAllBe(x => allowed.Contains(x));
        listening.ShouldNotContain(x => x.ToString().Contains("intake"));
    }

    [Fact]
    public void the_http_only_node_does_not_poll_scheduled_messages()
    {
        _messaging.GetRuntime().As<WolverineRuntime>().DurableScheduledJobs.ShouldNotBeNull();
        _httpOnly.GetRuntime().As<WolverineRuntime>().DurableScheduledJobs.ShouldBeNull();
    }

    [Fact]
    public async Task each_node_publishes_its_workloads_and_only_a_durability_node_the_durability_marker()
    {
        var nodes = await _everything.GetRuntime().Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);
        WolverineNode rowOf(IHost host) => nodes.Single(x => x.NodeId == host.GetRuntime().Options.UniqueNodeId);

        var everything = rowOf(_everything);
        everything.Workloads.ShouldBe(NodeWorkloads.All);
        everything.Capabilities.ShouldContain(MessageStoreCollection.DurabilityCapabilityUri);
        everything.Capabilities.ShouldNotContain(x => x.Host == NodeAgentController.WorkloadsMarkerHost);

        var messaging = rowOf(_messaging);
        messaging.Workloads.ShouldBe(NodeWorkloads.Messaging | NodeWorkloads.Projections);
        messaging.Capabilities.ShouldContain(new Uri("wolverine://node-workloads/messaging+projections"));
        messaging.Capabilities.ShouldNotContain(MessageStoreCollection.DurabilityCapabilityUri);

        var http = rowOf(_httpOnly);
        http.Workloads.ShouldBe(NodeWorkloads.None);
        http.Capabilities.ShouldContain(new Uri("wolverine://node-workloads/none"));
        http.Capabilities.ShouldNotContain(MessageStoreCollection.DurabilityCapabilityUri);
    }

    [Fact]
    public void only_the_nodes_running_messaging_may_lead()
    {
        _everything.GetRuntime().Options.Durability.CanLead.ShouldBeTrue();
        _messaging.GetRuntime().Options.Durability.CanLead.ShouldBeTrue();
        _httpOnly.GetRuntime().Options.Durability.CanLead.ShouldBeFalse();
    }

    /// <summary>
    ///     The #4923 shape: the durability agent recovers inbox envelopes through its holder's own listeners, so it
    ///     must never land on a node that has none, or on a node that only runs Messaging.
    /// </summary>
    [Fact]
    public async Task the_durability_agents_run_only_on_the_node_running_durability()
    {
        Func<Uri, bool> isDurability = uri => uri.Scheme == PersistenceConstants.AgentScheme;

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (!_everything.RunningAgents().Any(isDurability) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        _everything.RunningAgents().ShouldContain(x => isDurability(x));
        _messaging.RunningAgents().ShouldNotContain(x => isDurability(x));
        _httpOnly.RunningAgents().ShouldBeEmpty();
    }

    [Fact]
    public async Task the_http_only_node_refuses_to_start_an_agent_outside_its_workloads()
    {
        // What a leader that predates workloads could send it during a rolling upgrade
        var controller = _httpOnly.GetRuntime().NodeController!;

        var durability = await Should.ThrowAsync<InvalidAgentException>(() =>
            controller.StartAgentAsync(new Uri("wolverinedb://postgresql/localhost/postgres/node_workloads_5601")));
        durability.Message.ShouldContain("does not run the Durability workload");

        var listener = await Should.ThrowAsync<InvalidAgentException>(() =>
            controller.StartAgentAsync(new Uri("wolverine-listener://postgresql/intake")));
        listener.Message.ShouldContain("does not run the Messaging workload");

        _httpOnly.RunningAgents().ShouldBeEmpty();
    }
}
