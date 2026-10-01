using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

public class deduplication_store_compliance : DeduplicationStoreCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dedup_compliance");
    }

    protected override string deduplicationTableName => "dedup_compliance.wolverine_deduplication";

    protected override string hashedDeduplicationTableName =>
        "dedup_compliance.wolverine_deduplication_hashed";

    // GH-4757: SQL Server's default collation is case-INsensitive, which is half the reported defect.
    protected override bool stringComparisonIsCaseSensitive => false;
}
