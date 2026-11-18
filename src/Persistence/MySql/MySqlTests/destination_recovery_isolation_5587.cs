using IntegrationTests;
using Wolverine.MySql;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace MySqlTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "destination_isolation");
    }
}
