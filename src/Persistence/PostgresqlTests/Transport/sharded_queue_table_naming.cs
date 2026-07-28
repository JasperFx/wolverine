using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime.Partitioning;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-3468. PostgreSQL silently truncates identifiers past NAMEDATALEN (63 chars), so
/// a long sharded base name has to still produce distinct, in-range queue table names.
/// </summary>
public class sharded_queue_table_naming
{
    private static PartitionedMessageTopologyWithDatabaseQueues topologyFor(string baseName, int slots)
    {
        var options = new WolverineOptions();
        var transport = options.As<WolverineOptions>().Transports.GetOrCreate<PostgresqlTransport>();
        transport.TransportSchemaName = "gletters_naming";

        return new PartitionedMessageTopologyWithDatabaseQueues(options, PartitionSlots.Five, baseName, slots);
    }

    [Fact]
    public void short_base_name_uses_the_literal_table_names()
    {
        var topology = topologyFor("gletters", 4);

        topology.Slots.OfType<PostgresqlQueue>().Select(x => x.QueueTable.Identifier.Name)
            .ShouldBe(["wolverine_queue_gletters1", "wolverine_queue_gletters2", "wolverine_queue_gletters3",
                "wolverine_queue_gletters4"
            ]);
    }

    [Fact]
    public void long_base_name_shortens_to_distinct_identifiers()
    {
        // "wolverine_queue_" is 16 characters, so this base name blows past NAMEDATALEN
        var baseName = new string('a', 60);
        var topology = topologyFor(baseName, 4);

        var queues = topology.Slots.OfType<PostgresqlQueue>().ToArray();
        queues.Length.ShouldBe(4);

        var queueTableNames = queues.Select(x => x.QueueTable.Identifier.Name).ToArray();
        var scheduledTableNames = queues.Select(x => x.ScheduledTable.Identifier.Name).ToArray();

        foreach (var name in queueTableNames.Concat(scheduledTableNames))
        {
            name.Length.ShouldBeLessThanOrEqualTo(63);
        }

        // The whole point: shard 1 and shard 2 must not collapse onto the same table
        queueTableNames.Distinct().Count().ShouldBe(4);
        scheduledTableNames.Distinct().Count().ShouldBe(4);
        queueTableNames.Intersect(scheduledTableNames).Any().ShouldBeFalse();
    }
}
