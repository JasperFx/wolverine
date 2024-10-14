using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oakton;
using Shouldly;

namespace Wolverine.Kafka.Tests;

public class StatefulResourceSmokeTests
{
    private IHostBuilder ConfigureBuilder(bool autoProvision, int starting = 1)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (autoProvision)
                {
                    opts.UseKafka("localhost:9092").AutoProvision();
                }
                else
                {
                    opts.UseKafka("localhost:9092");;
                }

                opts.PublishMessage<SRMessage1>()
                    .ToKafkaTopic("sr" + starting++);

                opts.PublishMessage<SRMessage2>()
                    .ToKafkaTopic("sr" + starting++);

                opts.PublishMessage<SRMessage3>()
                    .ToKafkaTopic("sr" + starting++);

                opts.PublishMessage<SRMessage3>()
                    .ToKafkaTopic("sr" + starting++);
            });
    }

    [Fact]
    public async Task run_setup()
    {
        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(["resources", "setup"]);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task statistics()
    {
        (await ConfigureBuilder(false)
            .RunOaktonCommands(["resources", "setup"])).ShouldBe(0);

        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(["resources", "statistics"]);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task check_positive()
    {
        (await ConfigureBuilder(false)
            .RunOaktonCommands(["resources", "setup"])).ShouldBe(0);

        var result = await ConfigureBuilder(false)
            .RunOaktonCommands(["resources", "check"]);

        result.ShouldBe(0);
    }

    // [Fact]
    // public async Task check_negative()
    // {
    //     var result = await ConfigureBuilder(false, 10)
    //         .RunOaktonCommands(new[] { "resources", "check" });
    //
    //     result.ShouldBe(1);
    // }

    [Fact]
    public async Task clear_state()
    {
        (await ConfigureBuilder(false, 20)
            .RunOaktonCommands(["resources", "setup"])).ShouldBe(0);

        (await ConfigureBuilder(false, 20)
            .RunOaktonCommands(["resources", "clear"])).ShouldBe(0);
    }

    [Fact]
    public async Task teardown()
    {
        (await ConfigureBuilder(false, 30)
            .RunOaktonCommands(["resources", "setup"])).ShouldBe(0);

        (await ConfigureBuilder(false, 30)
            .RunOaktonCommands(["resources", "teardown"])).ShouldBe(0);
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