using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5126. The shared plumbing behind every Use{Transport}UsingNamedConnection(name) API: the value is read
/// from IConfiguration once the container exists and handed to whatever asked for it by name.
/// </summary>
public class named_connection_string_binding_5126
{
    private static IHostBuilder hostWith(Action<WolverineOptions> configure,
        params (string Name, string Value)[] connectionStrings)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                connectionStrings.Select(x =>
                    new KeyValuePair<string, string?>($"ConnectionStrings:{x.Name}", x.Value))))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                configure(opts);
            });
    }

    [Fact]
    public async Task the_resolved_value_is_applied_at_startup()
    {
        string? applied = null;

        using var host = await hostWith(
            opts => opts.ApplyNamedConnectionString("Widget", "widgets", "AddWidgets", x => applied = x),
            ("widgets", "widget://localhost")).StartAsync(TestContext.Current.CancellationToken);

        applied.ShouldBe("widget://localhost");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_missing_name_fails_startup_with_the_aggregated_exception()
    {
        var applied = false;

        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
            {
                opts.ApplyNamedConnectionString("Widget", "widgets", "AddWidgets", _ => applied = true);
                opts.RequireNamedConnectionString("Kafka", "kafka", "AddKafka");
            }).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["widgets", "kafka"]);
        ex.Message.ShouldContain("builder.AddWidgets(\"widgets\")");
        applied.ShouldBeFalse();
    }

    [Fact]
    public void registering_a_binding_also_registers_the_dependency_once()
    {
        var options = new WolverineOptions();

        options.ApplyNamedConnectionString("Widget", "widgets", "AddWidgets", _ => { });
        options.ApplyNamedConnectionString("Widget", "widgets", "AddWidgets", _ => { });

        options.NamedConfigurationDependencies.Single().ShouldBe(new NamedConfigurationDependency("Widget", "widgets", "AddWidgets"));
    }

    [Fact]
    public async Task applying_is_idempotent()
    {
        var count = 0;

        using var host = await hostWith(
            opts => opts.ApplyNamedConnectionString("Widget", "widgets", null, _ => count++),
            ("widgets", "widget://localhost")).StartAsync(TestContext.Current.CancellationToken);

        // e.g. the resources commands asking again after startup
        host.GetRuntime().ApplyNamedConnectionStrings();

        count.ShouldBe(1);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
