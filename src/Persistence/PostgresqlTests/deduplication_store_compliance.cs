using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class deduplication_store_compliance : DeduplicationStoreCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dedup_compliance");
    }

    protected override string deduplicationTableName => "dedup_compliance.wolverine_deduplication";

    // PostgreSQL's default collation compares bytes, so the string mode never had the defect here.
    protected override bool stringComparisonIsCaseSensitive => true;
}
