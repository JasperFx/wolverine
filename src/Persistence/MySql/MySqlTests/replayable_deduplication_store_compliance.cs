using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

namespace MySqlTests;

[Collection("mysql")]
public class replayable_deduplication_store_compliance : ReplayableDeduplicationStoreCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "dedup_responses");
    }
}
