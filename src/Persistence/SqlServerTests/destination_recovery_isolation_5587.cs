using IntegrationTests;
using Wolverine.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace SqlServerTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "destination_isolation");
    }
}
