using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;

namespace Wolverine.MQTT.Tests.Bugs;

public class Bug_1634_not_using_default_serializer_correctly
{
    [Fact]
    public async Task try_it_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // cfg.PublishMessagesToMqttTopic((Event e) => e.Id).DefaultSerializer(new Serializer());
                opts.PublishMessagesToMqttTopic((Event e) => e.Id);

                opts.UseMqttWithLocalBroker()
                    .ConfigureSenders(sub => sub.DefaultSerializer(new Serializer()));
            }).StartAsync();

        var runtime = host.GetRuntime();
        var topic = runtime.Options.Transports.GetOrCreate<MqttTransport>().Topics["One"];
        
        // This would have been done by creating a sender anyway
        topic.Compile(runtime);
        topic.DefaultSerializer.ShouldBeOfType<Serializer>();
    }
}

public record Event(string Id);

public class Serializer : IMessageSerializer
{
    public byte[] Write(Envelope envelope) => throw new NotImplementedException();

    public object ReadFromData(Type messageType, Envelope envelope) => throw new NotImplementedException();

    public object ReadFromData(byte[] data) => throw new NotImplementedException();

    public byte[] WriteMessage(object message) => throw new NotImplementedException();

    public string ContentType => "text/plain";
}