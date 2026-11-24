using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests.Durability;

/// <summary>
/// GH-5695. A durable partitioned local slot stores the copies of a message with several handler types as inbox rows,
/// with PostgreSQL as the message store
/// </summary>
public class durable_partitioned_slot_expansion_5695 : DurablePartitionedSlotExpansionCompliance
{
    protected override void PersistMessages(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "slots5695");
    }
}
