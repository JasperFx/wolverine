using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Oracle;

namespace OracleTests;

/// <summary>
/// GH-3590 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>.
/// </summary>
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }
}
