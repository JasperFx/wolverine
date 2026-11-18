using IntegrationTests;
using Wolverine.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace PostgresqlTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "destination_isolation");
    }
}
