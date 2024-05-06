using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Oakton;
using Oakton.Resources;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;

namespace PostgresqlTests.Transport;

[Collection("sqlserver")]
public class stateful_resource_smoke_tests : IAsyncLifetime
{
    private IHost _host;
    private IStatefulResource? theResource;
    private PostgresqlTransport theTransport;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        
        await conn.DropSchemaAsync("queues");
        await conn.CloseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private IHostBuilder ConfigureBuilder(bool autoProvision, int starting = 1)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (autoProvision)
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "postgres")
                        .AutoProvision();
                }
                else
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "postgres");
                }

                opts.PublishMessage<SRMessage1>()
                    .ToPostgresqlQueue("sr" + starting++);

                opts.PublishMessage<SRMessage2>()
                    .ToPostgresqlQueue("sr" + starting++);

                opts.PublishMessage<SRMessage3>()
                    .ToPostgresqlQueue("sr" + starting++);

                opts.PublishMessage<SRMessage3>()
                    .ToPostgresqlQueue("sr" + starting++);
            });
    }

    [Fact]
    public async Task run_setup()
    {
        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(new[] { "resources", "setup" });
        result.ShouldBe(0);
    }

    [Fact]
    public async Task statistics()
    {
        (await ConfigureBuilder(false)
            .RunOaktonCommands(new[] { "resources", "setup" })).ShouldBe(0);

        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(new[] { "resources", "statistics" });

        result.ShouldBe(0);
    }

    [Fact]
    public async Task check_positive()
    {
        (await ConfigureBuilder(false)
            .RunOaktonCommands(new[] { "resources", "setup" })).ShouldBe(0);

        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(new[] { "resources", "check" });

        result.ShouldBe(0);
    }

    [Fact]
    public async Task check_negative()
    {
        var result = await ConfigureBuilder(false, 10)
            .RunOaktonCommands(new[] { "resources", "check" });

        result.ShouldBe(1);
    }

    [Fact]
    public async Task clear_state()
    {
        (await ConfigureBuilder(false, 20)
            .RunOaktonCommands(new[] { "resources", "setup" })).ShouldBe(0);

        (await ConfigureBuilder(false, 20)
            .RunOaktonCommands(new[] { "resources", "clear" })).ShouldBe(0);
    }

    [Fact]
    public async Task teardown()
    {
        (await ConfigureBuilder(false, 30)
            .RunOaktonCommands(new[] { "resources", "setup" })).ShouldBe(0);

        (await ConfigureBuilder(false, 30)
            .RunOaktonCommands(new[] { "resources", "teardown" })).ShouldBe(0);
    }
}

public class SRMessage1;

public class SRMessage2;

public class SRMessage3;

public class SRMessage4;

public class SRMessageHandlers
{
    public Task Handle(SRMessage1 message)
    {
        return Task.Delay(100.Milliseconds());
    }

    public Task Handle(SRMessage2 message)
    {
        return Task.Delay(100.Milliseconds());
    }

    public Task Handle(SRMessage3 message)
    {
        return Task.Delay(100.Milliseconds());
    }

    public Task Handle(SRMessage4 message)
    {
        return Task.Delay(100.Milliseconds());
    }
}