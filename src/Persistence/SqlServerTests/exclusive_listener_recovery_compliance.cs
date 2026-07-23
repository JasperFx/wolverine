using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// GH-3590 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>.
/// </summary>
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "exclusive_recovery");
    }
}
