using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class configuration_precedence
{
    private readonly ITestOutputHelper _output;

    public configuration_precedence(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task explicit_configuration_wins()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").ConfigureConsumers( x => x.GroupId = "Conventional").AutoProvision();

                opts.ListenToKafkaTopic("General").Named("General");
                
                opts.ListenToKafkaTopic("ResponseMessages")
                    .ConfigureConsumer(x => x.GroupId = "Specific").Named("Specific"); // Not working as expected

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var runtime = host.GetRuntime();

        foreach (var agent in runtime.Endpoints.ActiveListeners())
        {
            _output.WriteLine(agent.Uri.ToString());
        }

        var general = runtime.Endpoints.EndpointFor("kafka://topic/General".ToUri()).ShouldBeOfType<KafkaTopic>();
        general.ConsumerConfig.ShouldBeNull();
        
        general.Parent.ConsumerConfig.GroupId.ShouldBe("Conventional");
        
        var specific = runtime.Endpoints.EndpointFor("kafka://topic/ResponseMessages".ToUri()).ShouldBeOfType<KafkaTopic>();
        specific.ConsumerConfig.GroupId.ShouldBe("Specific");
    }
}