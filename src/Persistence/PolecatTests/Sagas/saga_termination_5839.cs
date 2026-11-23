using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5839: completing a saga from outside its handlers, as Polecat documents
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "saga_termination";
        }).IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }
}
