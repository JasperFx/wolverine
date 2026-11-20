using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;

namespace MartenTests;

public class Marten_StorageCommand_Smoke_Tests : PostgresqlContext
{
    private static IHostBuilder buildHost()
    {
        return Host.CreateDefaultBuilder().UseWolverine(registry =>
        {
            registry.Discovery.DisableConventionalDiscovery();
            registry.Durability.Mode = DurabilityMode.Solo;
            registry.Services.AddMarten(Servers.PostgresConnectionString)
                .IntegrateWithWolverine();
        });
    }

    [Theory]
    [InlineData("storage rebuild")]
    [InlineData("storage counts")]
    [InlineData("storage clear")]
    [InlineData("storage release")]
    public async Task smoke_test_calls(string commandLine)
    {
        // GH-5676. The storage command builds its host but never starts it, so nothing provisions the envelope
        // tables. Another test in this suite can leave the public schema without them, and then "storage counts"
        // failed with 42P01 (relation "public.wolverine_incoming_envelopes" does not exist). Provision them here,
        // as SqlServerContext does for the SQL Server version of this test.
        using (var host = buildHost().Build())
        {
            await host.Services.GetRequiredService<IMessageStore>().Admin.MigrateAsync();
        }

        var args = commandLine.Split(' ');

        var exitCode = await buildHost().RunWolverineAsync(args);

        exitCode.ShouldBe(0);
    }
}
