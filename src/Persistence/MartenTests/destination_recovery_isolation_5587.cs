using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace MartenTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "destination_isolation_marten";
        }).IntegrateWithWolverine();
    }
}
