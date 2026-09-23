using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4527. Named connection strings used to be validated lazily, inside whichever DI factory happened to
/// consume them first -- so a host with two missing Aspire references failed twice, on two deploys, each
/// time after persistence had already migrated and any earlier transport had already connected. And the
/// message named the key and nothing else.
/// </summary>
public class named_connection_string_validation_4527
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
    public async Task every_missing_name_is_reported_in_one_throw()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
                {
                    opts.RequireNamedConnectionString("Rabbit MQ", "rabbit", "AddRabbitMQ");
                    opts.RequireNamedConnectionString("Kafka", "kafka", "AddKafka");
                },
                ("postgres", "Host=localhost")).StartAsync();
        });

        // Both, not just the first one resolved.
        ex.Missing.Select(x => x.Name).ShouldBe(["rabbit", "kafka"]);
        ex.Message.ShouldContain("2 connection strings");
        ex.Message.ShouldContain("'rabbit', required by the Rabbit MQ transport");
        ex.Message.ShouldContain("'kafka', required by the Kafka transport");
    }

    [Fact]
    public async Task the_message_names_the_keys_that_are_configured()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(
                opts => opts.RequireNamedConnectionString("Rabbit MQ", "rabbit", "AddRabbitMQ"),
                ("postgres", "Host=localhost"),
                ("messaging", "amqp://localhost")).StartAsync();
        });

        ex.Message.ShouldContain("Configured connection strings: 'messaging', 'postgres'.");

        // Keys only. A connection string is a credential and must never reach a log or an exception.
        ex.Message.ShouldNotContain("Host=localhost");
        ex.Message.ShouldNotContain("amqp://localhost");
        ex.ConfiguredNames.ShouldBe(["messaging", "postgres"]);
    }

    [Fact]
    public async Task the_message_names_the_aspire_remedy()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(
                opts => opts.RequireNamedConnectionString("Rabbit MQ", "rabbit", "AddRabbitMQ")).StartAsync();
        });

        // The dominant cause: a resource never added in the AppHost, or added and never referenced.
        ex.Message.ShouldContain("builder.AddRabbitMQ(\"rabbit\")");
        ex.Message.ShouldContain(".WithReference(...)");

        // ...and the non-Aspire answer too, for everyone else
        ex.Message.ShouldContain("ConnectionStrings:rabbit");
        ex.Message.ShouldContain("appsettings.json");
    }

    [Fact]
    public async Task no_configured_connection_strings_at_all_says_so()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(
                opts => opts.RequireNamedConnectionString("Kafka", "kafka", "AddKafka")).StartAsync();
        });

        ex.Message.ShouldContain("No connection strings are configured at all.");
    }

    [Fact]
    public async Task a_configured_name_starts_cleanly()
    {
        using var host = await hostWith(
            opts => opts.RequireNamedConnectionString("Kafka", "kafka", "AddKafka"),
            ("kafka", "localhost:9092")).StartAsync(TestContext.Current.CancellationToken);

        host.ShouldNotBeNull();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void declaring_the_same_dependency_twice_only_registers_it_once()
    {
        var options = new WolverineOptions();

        options.RequireNamedConnectionString("Kafka", "kafka", "AddKafka");
        options.RequireNamedConnectionString("Kafka", "kafka", "AddKafka");

        options.NamedConfigurationDependencies.Count.ShouldBe(1);
    }
}
