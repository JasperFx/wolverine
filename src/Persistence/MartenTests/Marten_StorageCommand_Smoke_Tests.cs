using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

public class Marten_StorageCommand_Smoke_Tests : PostgresqlContext
{
    [Theory]
    [InlineData("storage rebuild")]
    [InlineData("storage counts")]
    [InlineData("storage clear")]
    [InlineData("storage release")]
    public async Task smoke_test_calls(string commandLine)
    {
        var args = commandLine.Split(' ');

        var exitCode = await Host.CreateDefaultBuilder().UseWolverine(registry =>
        {
            registry.Services.AddMarten(Servers.PostgresConnectionString)
                .IntegrateWithWolverine();
        }).RunWolverineAsync(args);

        exitCode.ShouldBe(0);
    }
}