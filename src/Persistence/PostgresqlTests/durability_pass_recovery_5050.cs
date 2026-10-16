using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

/// <summary>
/// GH-5050 on PostgreSQL. See <see cref="DurabilityPassRecoveryCompliance" />
/// </summary>
public class durability_pass_recovery_5050 : DurabilityPassRecoveryCompliance
{
    protected override string SchemaName => "gh5050_pass_pg";

    protected override void ConfigurePersistence(WolverineOptions opts, string schemaName)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
    }
}
