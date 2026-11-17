using Xunit;

namespace CoreTests.Analyzers;

public class WOLV0004_message_bus_from_host_services
{
    private const string Source = """
        using System;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Wolverine;
        using Wolverine.Runtime;

        public class Specs
        {
            public void Act(IHost host)
            {
                var one = host.Services.GetRequiredService<IMessageBus>();
                var two = host.Services.GetService<IMessageBus>();
                var three = host.Services.GetRequiredService(typeof(IMessageBus));
                var four = host.Services.GetService(typeof(IMessageBus));

                // All fine
                var bus = host.MessageBus();
                var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
                using var scope = host.Services.CreateScope();
                var scoped = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            }
        }
        """;

    [Fact]
    public async Task resolving_the_bus_from_the_host_in_a_test_project()
    {
        var diagnostics = await AnalyzerHarness.DiagnosticsFor(Source, asTestProject: true);

        diagnostics.Select(x => x.Describe()).ShouldBe([
            "WOLV0004: host.Services.GetRequiredService<IMessageBus>()",
            "WOLV0004: host.Services.GetService<IMessageBus>()",
            "WOLV0004: host.Services.GetRequiredService(typeof(IMessageBus))",
            "WOLV0004: host.Services.GetService(typeof(IMessageBus))"
        ]);
    }

    [Fact]
    public async Task not_reported_outside_a_test_project()
    {
        (await AnalyzerHarness.DiagnosticsFor(Source, asTestProject: false)).ShouldBeEmpty();
    }
}
