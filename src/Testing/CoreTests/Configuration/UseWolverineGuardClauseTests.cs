using Microsoft.Extensions.Hosting;
using TestingSupport;
using Xunit;

namespace CoreTests.Configuration;

public class UseWolverineGuardClauseTests
{
    [Fact]
    public void cannot_call_use_wolverine_twice()
    {
        var builder = Host.CreateDefaultBuilder().UseWolverine(o => { });

        builder.UseWolverine(o => { });

        Exception<InvalidOperationException>.ShouldBeThrownBy(() => builder.UseWolverine().Start());
    }
}