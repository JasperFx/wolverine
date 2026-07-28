using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Partitioning;
using Wolverine.SqlServer.Transport;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-3469. The sharded topology opts its own queues into the seq-clustered high throughput
/// layout without disturbing anything else in the transport, and shard-suffixed table names
/// stay well inside SQL Server's 128 character identifier limit.
/// </summary>
public class sharded_queue_table_layout
{
    private static (SqlServerTransport, PartitionedMessageTopologyWithDatabaseQueues) topologyFor(string baseName,
        int slots)
    {
        var options = new WolverineOptions();
        var transport = new SqlServerTransport(new DatabaseSettings { SchemaName = "gletters_layout" });
        options.As<WolverineOptions>().Transports.Add(transport);

        var topology = new PartitionedMessageTopologyWithDatabaseQueues(options, PartitionSlots.Five, baseName, slots);
        return (transport, topology);
    }

    [Fact]
    public void shard_queues_default_to_the_high_throughput_layout()
    {
        var (transport, topology) = topologyFor("gletters", 4);

        foreach (var queue in topology.Slots.OfType<SqlServerQueue>())
        {
            queue.OptimizeThroughput.ShouldBeTrue();
            queue.QueueTable.Columns.Any(x => x.Name == "seq").ShouldBeTrue();
            queue.ScheduledTable.Columns.Any(x => x.Name == "seq").ShouldBeTrue();
        }

        // Per queue, not transport-wide -- an unrelated queue keeps the default layout
        transport.OptimizeQueueThroughput.ShouldBeFalse();
        transport.Queues["unrelated"].OptimizeThroughput.ShouldBeFalse();
        transport.Queues["unrelated"].QueueTable.Columns.Any(x => x.Name == "seq").ShouldBeFalse();
    }

    [Fact]
    public void can_opt_the_shard_queues_back_out()
    {
        var (_, topology) = topologyFor("gletters", 4);
        topology.OptimizeThroughput = false;

        foreach (var queue in topology.Slots.OfType<SqlServerQueue>())
        {
            queue.OptimizeThroughput.ShouldBeFalse();
            queue.QueueTable.Columns.Any(x => x.Name == "seq").ShouldBeFalse();
        }
    }

    [Fact]
    public void shard_table_names_are_distinct_and_within_the_identifier_limit()
    {
        // "wolverine_queue_" (16) + base name + shard suffix + "_scheduled" (10)
        var baseName = new string('a', 90);
        var (_, topology) = topologyFor(baseName, 4);

        var queues = topology.Slots.OfType<SqlServerQueue>().ToArray();
        queues.Length.ShouldBe(4);

        var names = queues.Select(x => x.QueueTable.Identifier.Name)
            .Concat(queues.Select(x => x.ScheduledTable.Identifier.Name))
            .ToArray();

        foreach (var name in names)
        {
            name.Length.ShouldBeLessThanOrEqualTo(128);
        }

        names.Distinct().Count().ShouldBe(names.Length);
    }
}
