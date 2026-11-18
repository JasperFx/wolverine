using IntegrationTests;
using Wolverine.Oracle;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace OracleTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }
}
