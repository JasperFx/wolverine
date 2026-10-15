using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// GH-5050 on SQL Server. See <see cref="DurabilityPassRecoveryCompliance" />
/// </summary>
public class durability_pass_recovery_5050 : DurabilityPassRecoveryCompliance
{
    protected override string SchemaName => "gh5050_pass_ss";

    protected override void ConfigurePersistence(WolverineOptions opts, string schemaName)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName);
    }
}
