using System.Text;
using JasperFx.Core;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Internal;
using MQTTnet.Protocol;
using Wolverine.ComplianceTests;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class connectivity
{
    private readonly ITestOutputHelper _output;

    public connectivity(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task can_connect_to_a_local_broker()
    {
        var port = PortFinder.GetAvailablePort();
        await using var broker = new LocalMqttBroker(port)
        {
            Logger = new XUnitLogger(_output, "MQTT")
        };
        await broker.StartAsync();

        var managedClient = new MqttFactory().CreateManagedMqttClient();

        managedClient.ApplicationMessageReceivedAsync += e =>
        {
            _output.WriteLine(">> RECEIVED: " + e.ApplicationMessage.Topic + ", " + Encoding.Default.GetString(e.ApplicationMessage.PayloadSegment));
            return CompletedTask.Instance;
        };


        await managedClient.StartAsync(new ManagedMqttClientOptionsBuilder().WithClientOptions(o => o.WithTcpServer("127.0.0.1", port)).Build());

        await managedClient.SubscribeAsync("Step");

        //await Task.Delay(5.Seconds());

        await managedClient.EnqueueAsync(topic: "Step", payload: "1", MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        await managedClient.EnqueueAsync(topic: "Step", payload: "2", MqttQualityOfServiceLevel.AtLeastOnce, retain: true);

        await Task.Delay(3.Seconds());

        await managedClient.SubscribeAsync(topic: "xyz", qualityOfServiceLevel: MqttQualityOfServiceLevel.AtMostOnce);
        await managedClient.SubscribeAsync(topic: "abc", qualityOfServiceLevel: MqttQualityOfServiceLevel.AtMostOnce);

        await managedClient.EnqueueAsync(topic: "Step", payload: "3");


        await Task.Delay(1.Minutes());

        // var transport = new MqttTransport();
        // transport.Configuration = builder =>
        // {
        //     builder.WithClientOptions(x =>
        //     {
        //         x.WithTcpServer("127.0.0.1", port);
        //     });
        // };
        //
        // await transport.InitializeAsync(Substitute.For<IWolverineRuntime>());
        //
        // await transport.DisposeAsync();


    }
}