using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Marten;

namespace MartenTests;

/// <summary>
/// GH-3590 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>.
/// </summary>
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "exclusive_recovery_marten";
        }).IntegrateWithWolverine();
    }
}
