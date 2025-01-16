using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;

namespace PostgresqlTests;

public class ConfigureWithDefaultSchema
{
    [Fact]
    public async Task should_use_public_as_default_schema()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // no schema provided
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString)
                    .AutoProvision();
            }).StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Options.Transports.OfType<DatabaseControlTransport>().Single()
            .Database.Settings.SchemaName.ShouldBe("public");
    }
}