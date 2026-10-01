using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace PersistenceTests.Agents;

/// <summary>
/// Regression test for GH-4734: "Envelopes executed twice when a node starts on a PostgreSQL queue backlog".
///
/// <para>
/// A Balanced node used to register itself -- claiming its row in the node table and with it the
/// AssignedNodeNumber -- only AFTER the messaging transports had started. Until then
/// <c>Durability.AssignedNodeNumber</c> held the per-process default, a hash of a fresh Guid, and that is the
/// number listeners stamp onto every inbox row they claim (PostgresqlQueueListener's queue-to-inbox hand-off,
/// DurableLocalQueue, ListeningAgent, the sending agents). So a node starting on a queue backlog took
/// ownership of it under a number that was in no node table.
/// </para>
///
/// <para>
/// The orphaned-message sweep then released those rows as "previously owned by departed nodes" -- correctly,
/// by its own rule -- and the ones not yet processed ran a second time with the same envelope id. One node was
/// enough and no node had to fail. The premise ReleaseOrphanedMessagesCommand states for the main-database
/// path, that "an owner_id can only appear in the envelope table if that node's registration had already
/// committed", was exactly what the startup order falsified.
/// </para>
///
/// <para>
/// So the thing to pin is the ORDER, not the symptom. The symptom needs a 15-to-20-second sweep cycle and a
/// race the reporter measured at a few milliseconds locally -- it reproduced in 12 of 14 of their runs, which
/// is not a regression test. This asserts the invariant instead, at the earliest point inside the transport
/// startup phase that production code can be observed from: by the time any transport initializes, let alone
/// a listener starts consuming, the node number is the registered one.
/// </para>
/// </summary>
public class Bug_4734_node_registers_before_the_transports_start : PostgresqlContext, IAsyncDisposable
{
    private const string SchemaName = "bug_4734";
    private IHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task the_node_number_is_already_registered_when_the_transports_start()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        await conn.CloseAsync();

        var probe = new NodeNumberProbeTransport();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Discovery.DisableConventionalDiscovery();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();

                opts.Transports.Add(probe);
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        var assigned = runtime.Options.Durability.AssignedNodeNumber;

        probe.NodeNumberAtInitialize.ShouldNotBeNull("The probe transport was never initialized");

        probe.NodeNumberAtInitialize.Value.ShouldBe(assigned,
            "The transports started before this node had registered, so every inbox row a listener claimed in the meantime was stamped with a node number that no node table has ever held");

        // And the number is a real one -- the only thing that makes the orphaned-message sweep's rule safe
        var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(TestContext.Current.CancellationToken);
        nodes.Select(x => x.AssignedNodeNumber)
            .ShouldContain(assigned, "An owner_id absent from the node table is swept as a departed node's");
    }
}

/// <summary>
/// Records Durability.AssignedNodeNumber at the moment transport startup begins. It owns no endpoints -- all
/// it is here for is the <c>InitializeAsync</c> call every transport gets from
/// <c>startMessagingTransportsAsync</c>, which runs before any listener starts consuming.
/// </summary>
internal class NodeNumberProbeTransport : TransportBase<Endpoint>
{
    public NodeNumberProbeTransport() : base("node-number-probe", "Node Number Probe", [])
    {
    }

    public int? NodeNumberAtInitialize { get; private set; }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        NodeNumberAtInitialize = runtime.Options.Durability.AssignedNodeNumber;
        return base.InitializeAsync(runtime);
    }

    protected override IEnumerable<Endpoint> endpoints() => [];

    protected override Endpoint findEndpointByUri(Uri uri) =>
        throw new NotSupportedException("This transport exists only to observe startup ordering");
}
