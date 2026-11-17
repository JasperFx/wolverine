using JasperFx.Descriptors;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class locally_owned_databases
{
    private static readonly DatabaseId Claims129 = new("server-1", "claims129_productie");
    private static readonly DatabaseId Claims130 = new("server-1", "claims130_productie");

    private static IWolverineRuntime runtimeFor(DurabilityMode mode, params DatabaseId[] owned)
    {
        var agents = Substitute.For<IAgentRuntime>();
        agents.AllLocallyOwnedDatabaseIds().Returns(owned);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions { Durability = { Mode = mode } });
        runtime.Agents.Returns(agents);
        return runtime;
    }

    [Fact]
    public void balanced_node_owns_only_the_databases_of_its_agents()
    {
        var owned = runtimeFor(DurabilityMode.Balanced, Claims129).LocallyOwnedDatabases();

        owned.Contains(Claims129).ShouldBeTrue();
        owned.Contains(Claims130).ShouldBeFalse();
        owned.IsEvery.ShouldBeFalse();
        owned.IsNone.ShouldBeFalse();
        owned.Ids.ShouldBe([Claims129]);
    }

    [Fact]
    public void a_database_is_matched_by_server_and_name_not_by_name_alone()
    {
        var owned = runtimeFor(DurabilityMode.Balanced, Claims129).LocallyOwnedDatabases();

        owned.Contains(new DatabaseId("server-2", Claims129.Name)).ShouldBeFalse();
    }

    [Fact]
    public void a_shard_identifier_is_not_a_database_id()
    {
        // Marten's IMartenDatabase.Identifier ("claims129") is not what agent URIs carry
        var owned = runtimeFor(DurabilityMode.Balanced, Claims129).LocallyOwnedDatabases();

        owned.Contains(new DatabaseId("server-1", "claims129")).ShouldBeFalse();
    }

    [Fact]
    public void balanced_node_without_agents_owns_nothing()
    {
        var owned = runtimeFor(DurabilityMode.Balanced).LocallyOwnedDatabases();

        owned.IsNone.ShouldBeTrue();
        owned.IsEvery.ShouldBeFalse();
        owned.Contains(Claims129).ShouldBeFalse();
    }

    [Fact]
    public void solo_node_owns_every_database()
    {
        var owned = runtimeFor(DurabilityMode.Solo).LocallyOwnedDatabases();

        owned.IsEvery.ShouldBeTrue();
        owned.IsNone.ShouldBeFalse();
        owned.Contains(Claims129).ShouldBeTrue();
        owned.Contains(Claims130).ShouldBeTrue();
    }

    [Fact]
    public void it_is_read_fresh_on_every_call()
    {
        var runtime = runtimeFor(DurabilityMode.Balanced, Claims129);
        runtime.LocallyOwnedDatabases().Contains(Claims129).ShouldBeTrue();

        runtime.Agents.AllLocallyOwnedDatabaseIds().Returns([Claims130]);

        var after = runtime.LocallyOwnedDatabases();
        after.Contains(Claims129).ShouldBeFalse();
        after.Contains(Claims130).ShouldBeTrue();
    }

    [Theory]
    [InlineData(DurabilityMode.Serverless)]
    [InlineData(DurabilityMode.ServerlessWithOutbox)]
    [InlineData(DurabilityMode.MediatorOnly)]
    public async Task modes_that_run_no_agents_own_nothing(DurabilityMode mode)
    {
        // No node agents ever run in these modes, so IsNone is the final answer here, not "nothing assigned yet"
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var owned = host.GetRuntime().LocallyOwnedDatabases();

        owned.IsNone.ShouldBeTrue();
        owned.IsEvery.ShouldBeFalse();
        owned.Ids.ShouldBeEmpty();
        owned.Contains(Claims129).ShouldBeFalse();
    }
}
