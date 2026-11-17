using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents.Workloads;

/// <summary>
///     GH-5601. The pieces under the node workload specifications that read better as plain tests: the marker a node
///     publishes, what each node advertises, which workload each of Wolverine's agent families needs, and the
///     presets.
/// </summary>
public class node_workloads_unit_tests_5601
{
    public static TheoryData<NodeWorkloads> EveryCombination()
    {
        var data = new TheoryData<NodeWorkloads>();
        for (var i = 0; i <= (int)NodeWorkloads.All; i++) data.Add((NodeWorkloads)i);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void the_marker_round_trips(NodeWorkloads workloads)
    {
        var marker = NodeAgentController.WorkloadsMarkerFor(workloads);
        var capabilities = marker == null ? new List<Uri>() : [marker];

        NodeAgentController.WorkloadsOf(capabilities).ShouldBe(workloads);
    }

    [Fact]
    public void the_marker_reads_the_way_a_person_would_write_it()
    {
        NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.None)
            .ShouldBe(new Uri("wolverine://node-workloads/none"));
        NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.Messaging | NodeWorkloads.Projections)
            .ShouldBe(new Uri("wolverine://node-workloads/messaging+projections"));
    }

    [Fact]
    public void all_publishes_no_marker_so_a_default_node_looks_like_any_6x_node()
    {
        NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.All).ShouldBeNull();
        NodeAgentController.WorkloadsOf([MessageStoreCollection.DurabilityCapabilityUri]).ShouldBe(NodeWorkloads.All);
    }

    [Fact]
    public void a_workload_a_newer_version_adds_is_ignored()
    {
        NodeAgentController.WorkloadsOf([new Uri("wolverine://node-workloads/messaging+teleportation")])
            .ShouldBe(NodeWorkloads.Messaging);
    }

    [Fact]
    public void the_marker_is_a_node_marker_not_an_agent()
    {
        NodeAgentController.IsNodeMarker(NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.None)!).ShouldBeTrue();
    }

    [Fact]
    public void wolverine_node_reads_its_workloads_from_its_capabilities()
    {
        var node = new WolverineNode();
        node.Workloads.ShouldBe(NodeWorkloads.All);

        node.Capabilities.Add(NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.Messaging)!);
        node.Workloads.ShouldBe(NodeWorkloads.Messaging);
    }

    [Fact]
    public void the_workloads_of_a_wolverine_node_are_not_serialized_on_their_own()
    {
        var node = new WolverineNode();
        node.Capabilities.Add(NodeAgentController.WorkloadsMarkerFor(NodeWorkloads.None)!);

        JsonSerializer.Serialize(node).ShouldNotContain("\"Workloads\"");
    }

    private static readonly Uri Listener = new("wolverine-listener://rabbitmq/orders");
    private static readonly Uri Projection = new("event-subscriptions://marten/main/main/trips/all/v1");

    private static List<Uri> capabilitiesOf(DurabilitySettings settings)
    {
        var listeners = new WorkloadCluster.StaticHarnessFamily("wolverine-listener", NodeWorkloads.Messaging, (AssignmentGrid _, string _) => { });
        var projections = new WorkloadCluster.StaticHarnessFamily("event-subscriptions", NodeWorkloads.Projections, (AssignmentGrid _, string _) => { });

        return NodeAgentController.BuildCapabilities(settings,
            [(listeners, [Listener]), (projections, [Projection])]);
    }

    [Fact]
    public void a_default_node_advertises_everything_and_no_workloads_marker()
    {
        var capabilities = capabilitiesOf(new DurabilitySettings());

        capabilities.ShouldBe([Listener, Projection, MessageStoreCollection.DurabilityCapabilityUri], ignoreOrder: true);
    }

    [Fact]
    public void an_http_only_node_advertises_only_its_marker()
    {
        var settings = new DurabilitySettings();
        settings.RunAsHttpOnlyNode();

        capabilitiesOf(settings).ShouldBe([new Uri("wolverine://node-workloads/none")]);
    }

    [Fact]
    public void a_messaging_node_withholds_the_durability_marker()
    {
        var settings = new DurabilitySettings();
        settings.RunAsMessagingNode();

        capabilitiesOf(settings).ShouldBe(
            [Listener, Projection, new Uri("wolverine://node-workloads/messaging+projections")], ignoreOrder: true);
    }

    [Fact]
    public void a_node_without_projections_withholds_the_projection_agents()
    {
        var settings = new DurabilitySettings { Workloads = NodeWorkloads.Messaging | NodeWorkloads.Durability };

        capabilitiesOf(settings).ShouldBe(
            [Listener, MessageStoreCollection.DurabilityCapabilityUri, new Uri("wolverine://node-workloads/messaging+durability")],
            ignoreOrder: true);
    }

    [Fact]
    public void durability_agent_enabled_false_still_withholds_the_durability_marker()
    {
        var capabilities = capabilitiesOf(new DurabilitySettings { DurabilityAgentEnabled = false });

        capabilities.ShouldNotContain(MessageStoreCollection.DurabilityCapabilityUri);
    }

    [Fact]
    public void the_presets_set_the_workloads()
    {
        var settings = new DurabilitySettings();

        settings.RunAsHttpOnlyNode();
        settings.Workloads.ShouldBe(NodeWorkloads.None);

        settings.RunAsMessagingNode();
        settings.Workloads.ShouldBe(NodeWorkloads.Messaging | NodeWorkloads.Projections);

        settings.RunAsDurabilityNode();
        settings.Workloads.ShouldBe(NodeWorkloads.All);
    }

    [Fact]
    public void the_workloads_parse_from_configuration_text()
    {
        // What Wolverine__Workloads=Messaging,Projections turns into through the configuration binder
        Enum.Parse<NodeWorkloads>("Messaging, Projections").ShouldBe(NodeWorkloads.Messaging | NodeWorkloads.Projections);
        Enum.Parse<NodeWorkloads>("Messaging,Projections").ShouldBe(NodeWorkloads.Messaging | NodeWorkloads.Projections);
        Enum.Parse<NodeWorkloads>("None").ShouldBe(NodeWorkloads.None);
    }

    [Fact]
    public void the_durability_families_need_durability()
    {
        requiredWorkloadOf<MessageStoreCollection>().ShouldBe(NodeWorkloads.Durability);
        requiredWorkloadOf<MultiTenantedMessageStore>().ShouldBe(NodeWorkloads.Durability);
    }

    [Fact]
    public void the_event_subscription_family_needs_projections()
    {
        requiredWorkloadOf<EventSubscriptionAgentFamily>().ShouldBe(NodeWorkloads.Projections);
    }

    [Fact]
    public void every_other_family_is_messaging_work()
    {
        requiredWorkloadOf<ExclusiveListenerFamily>().ShouldBe(NodeWorkloads.Messaging);
        requiredWorkloadOf<LeaderPinnedListenerFamily>().ShouldBe(NodeWorkloads.Messaging);
        requiredWorkloadOf<DynamicListenerAgentFamily>().ShouldBe(NodeWorkloads.Messaging);
        requiredWorkloadOf<Wolverine.Runtime.Recurring.RecurringTimerAgentFamily>().ShouldBe(NodeWorkloads.Messaging);
        requiredWorkloadOf<Wolverine.Runtime.Recurring.RecurringMessageAgent>().ShouldBe(NodeWorkloads.Messaging);
    }

    // The property is constant per family, so no constructor (and none of what it needs) has to run
    private static NodeWorkloads requiredWorkloadOf<T>() where T : IAgentFamily =>
        ((IAgentFamily)RuntimeHelpers.GetUninitializedObject(typeof(T))).RequiredWorkload;

    [Fact]
    public void the_grid_knows_the_workload_of_the_well_known_schemes_without_being_told()
    {
        var grid = new AssignmentGrid();

        grid.RequiredWorkloadFor(PersistenceConstants.AgentScheme).ShouldBe(NodeWorkloads.Durability);
        grid.RequiredWorkloadFor(EventSubscriptionAgentFamily.SchemeName).ShouldBe(NodeWorkloads.Projections);
        grid.RequiredWorkloadFor("anything-else").ShouldBe(NodeWorkloads.Messaging);

        grid.RequireWorkload("anything-else", NodeWorkloads.Projections);
        grid.RequiredWorkloadFor("anything-else").ShouldBe(NodeWorkloads.Projections);
    }

    [Fact]
    public void describe_shows_the_workloads()
    {
        var settings = new DurabilitySettings();
        settings.RunAsMessagingNode();

        settings.ToDescription().Properties.ShouldContain(x => x.Name == nameof(DurabilitySettings.Workloads) &&
                                                               x.Value == "Messaging, Projections");

        new Wolverine.Configuration.Capabilities.DurabilitySettingsDescription(settings).Workloads
            .ShouldBe("Messaging, Projections");
    }
}
