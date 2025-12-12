using JasperFx;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Acceptance;

public class capabilities_command
{
    [Fact]
    public async Task smoke_test_the_command()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine();

        var exitCode = await builder.RunJasperFxCommands(["capabilities"]);
        
        exitCode.ShouldBe(0);
    }
}