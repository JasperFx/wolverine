using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

namespace MySqlTests;

[Collection("mysql")]
public class deduplication_store_compliance : DeduplicationStoreCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "dedup_compliance");
    }

    protected override string deduplicationTableName => "dedup_compliance.wolverine_deduplication";

    protected override string hashedDeduplicationTableName =>
        "dedup_compliance.wolverine_deduplication_hashed";

    // GH-4757: MySQL's default collation is case- AND accent-insensitive, the other half of the report.
    protected override bool stringComparisonIsCaseSensitive => false;
}
