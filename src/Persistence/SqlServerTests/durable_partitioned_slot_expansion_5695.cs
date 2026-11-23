using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// GH-5695. A durable partitioned local slot stores the copies of a message with several handler types as inbox rows,
/// with SQL Server as the message store. See <see cref="DurablePartitionedSlotExpansionCompliance" />
/// </summary>
public class durable_partitioned_slot_expansion_5695 : DurablePartitionedSlotExpansionCompliance
{
    protected override void PersistMessages(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "slots5695");
    }
}
