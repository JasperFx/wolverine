using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

public class topology_scoped_grouping
{
    private static string? groupIdOf(WolverineOptions options, object message, string? tenantId = null)
    {
        var envelope = new Envelope(message) { TenantId = tenantId };
        return options.MessagePartitioning.DetermineGroupId(envelope);
    }

    [Fact]
    public void a_topology_grouped_by_tenant_does_not_reach_messages_outside_it()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("tenants", 3, topology =>
        {
            topology.Message<Coffee1>();
            topology.GroupByTenantId();
        });

        // Declared after the topology, which is exactly where an application-wide ByTenantId()
        // would have shadowed it
        options.MessagePartitioning.ByMessage<Coffee3>(x => x.Name);

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's"), "red").ShouldBe("red");
        groupIdOf(options, new Coffee3("Starbucks"), "red").ShouldBe("Starbucks");
    }

    [Fact]
    public void a_topology_rule_wins_over_an_application_wide_catch_all_declared_before_it()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.ByTenantId();
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("coffee", 3, topology =>
        {
            topology.Message<Coffee1>();
            topology.GroupBy<Coffee1>(x => x.Brand);
        });

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's"), "red").ShouldBe("Paul Newman's");
    }

    [Fact]
    public void a_topology_with_its_own_rules_does_not_fall_back_to_the_application_wide_rules()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.ByMessage<Coffee1>(x => x.Brand);
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("tenants", 3, topology =>
        {
            topology.Message<Coffee1>();
            topology.GroupByTenantId();
        });

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's"), tenantId: null).ShouldBeNull();
    }

    [Fact]
    public void rules_on_one_topology_are_evaluated_in_declaration_order()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("coffee", 3, topology =>
        {
            topology.MessagesImplementing<ICoffee>();
            topology.GroupBy<Coffee1>(x => x.Brand);
            topology.GroupBy<ICoffee>(x => x.Name);
        });

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's")).ShouldBe("Paul Newman's");
        groupIdOf(options, new Coffee2("Light")).ShouldBe("Light");
    }

    [Fact]
    public void a_topology_without_rules_keeps_using_the_application_wide_rules()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.ByMessage<Coffee1>(x => x.Brand);
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("coffee", 3,
            topology => topology.Message<Coffee1>());
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("tenants", 3, topology =>
        {
            topology.Message<Coffee3>();
            topology.GroupByTenantId();
        });

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's"), "red").ShouldBe("Paul Newman's");
    }

    [Fact]
    public void an_explicit_group_id_still_wins()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("tenants", 3, topology =>
        {
            topology.Message<Coffee1>();
            topology.GroupByTenantId();
        });

        var envelope = new Envelope(new Coffee1("Dark", "Paul Newman's")) { TenantId = "red", GroupId = "Code Red" };

        options.MessagePartitioning.DetermineGroupId(envelope).ShouldBe("Code Red");
    }

    [Fact]
    public void a_global_partitioned_topology_can_declare_its_own_grouping()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.ByTenantId();

        var topology = new GlobalPartitionedMessageTopology(options);
        topology.Message<Coffee1>();
        topology.GroupBy<Coffee1>(x => x.Brand);
        options.MessagePartitioning.GlobalPartitionedTopologies.Add(topology);

        groupIdOf(options, new Coffee1("Dark", "Paul Newman's"), "red").ShouldBe("Paul Newman's");
        groupIdOf(options, new Coffee3("Starbucks"), "red").ShouldBe("red");
    }

    [Fact]
    public void two_topologies_that_both_declare_grouping_for_one_message_type_are_rejected()
    {
        var options = new WolverineOptions();
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("tenants", 3, topology =>
        {
            topology.Message<Coffee1>();
            topology.GroupByTenantId();
        });
        options.MessagePartitioning.PublishToPartitionedLocalMessaging("brands", 3, topology =>
        {
            topology.MessagesImplementing<ICoffee>();
            topology.GroupBy<ICoffee>(x => x.Name);
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            options.MessagePartitioning.AssertTopologyGroupingIsUnambiguous([typeof(Coffee1)]));

        ex.Message.ShouldContain("tenants");
        ex.Message.ShouldContain("brands");

        // A type only one of them declares grouping for is fine
        options.MessagePartitioning.AssertTopologyGroupingIsUnambiguous([typeof(Coffee2)]);
    }

    [Fact]
    public async Task the_envelope_routed_to_a_slot_carries_the_topology_group_id()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MessagePartitioning.ByTenantId();
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("scopedcoffee", 3, topology =>
                {
                    topology.Message<Coffee1>();
                    topology.GroupBy<Coffee1>(x => x.Brand);
                });
            }).StartAsync(TestContext.Current.CancellationToken);

        var envelope = host.MessageBus()
            .PreviewSubscriptions(new Coffee1("Dark", "Paul Newman's"), new DeliveryOptions { TenantId = "red" })
            .Single();

        envelope.GroupId.ShouldBe("Paul Newman's");
        envelope.Destination!.ToString().ShouldStartWith("local://scopedcoffee");
    }

    [Fact]
    public async Task an_ambiguous_topology_grouping_fails_the_host_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.IncludeType(typeof(ScopedGroupingProbeHandler));
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("probes-a", 3, topology =>
                    {
                        topology.Message<ScopedGroupingProbe>();
                        topology.GroupByTenantId();
                    });
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("probes-b", 3, topology =>
                    {
                        topology.Message<ScopedGroupingProbe>();
                        topology.GroupBy<ScopedGroupingProbe>(x => x.Id);
                    });
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(ScopedGroupingProbe));
    }
}

public record ScopedGroupingProbe(string Id);

public static class ScopedGroupingProbeHandler
{
    public static void Handle(ScopedGroupingProbe probe)
    {
    }
}
