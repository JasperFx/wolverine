using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class replayable_deduplication_store_compliance : ReplayableDeduplicationStoreCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dedup_responses");
    }
}
