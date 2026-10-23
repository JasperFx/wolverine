using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Spectre.Console;
using Wolverine;
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

/// <summary>
/// GH-5156. describe lists every named connection string and whether it resolves -- never the value.
/// </summary>
public class describe_named_connection_strings_5156
{
    [Fact]
    public async Task lists_resolved_and_missing_names_without_values()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbit"] = "amqp://secret-user:secret-password@localhost"
            }))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.RequireNamedConnectionString("Rabbit MQ", "rabbit", "AddRabbitMQ");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        // Declared after startup so the host can start, the way describe sees an application whose
        // configuration is missing a name
        runtime.Options.RequireNamedConnectionString("Kafka", "kafka", "AddKafka");

        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = 200;

        new WolverineSystemPart(runtime).WriteNamedConnectionStrings(console).ShouldBeTrue();

        var rendered = writer.ToString();
        rendered.ShouldContain("Named Connection Strings");
        rendered.ShouldContain("rabbit");
        rendered.ShouldContain("Rabbit MQ transport");
        rendered.ShouldContain("Resolved");
        rendered.ShouldContain("kafka");
        rendered.ShouldContain("Missing");
        rendered.ShouldContain("builder.AddKafka(\"kafka\")");

        rendered.ShouldNotContain("secret-password");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task writes_nothing_without_named_connection_strings()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Durability.Mode = DurabilityMode.Solo)
            .StartAsync(TestContext.Current.CancellationToken);

        new WolverineSystemPart(host.GetRuntime()).WriteNamedConnectionStrings().ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

}
