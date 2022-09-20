using System.Threading.Tasks;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.SqlServer;
using Xunit;

namespace Wolverine.Persistence.Testing.SqlServer;

public class SqlServer_StorageCommand_Smoke_Tests : SqlServerContext
{
    [Theory]
    [InlineData("storage rebuild")]
    [InlineData("storage counts")]
    [InlineData("storage clear")]
    [InlineData("storage release")]
    public async Task smoke_test_calls(string commandLine)
    {
        var args = commandLine.Split(' ');
        (await Host.CreateDefaultBuilder().UseWolverine(registry =>
        {
            registry.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
        }).RunWolverineAsync(args)).ShouldBe(0);
    }
}
