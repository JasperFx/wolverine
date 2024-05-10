using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Tcp;

namespace DocumentationSamples;

public class EndpointConfiguration
{
    public async Task configure_subscriptions()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<MyMessageOne>().ToPort(3333)

                    // Override the endpoint name easier usage and diagnostics
                    .Named("threes")

                    // Configure the outgoing circuit breaker
                    // rules
                    .CircuitBreaking(cb => { cb.FailuresBeforeCircuitBreaks = 5; })

                    // Customize the outgoing message envelopes to add headers
                    // or other delivery options
                    .CustomizeOutgoing(env => { env.Headers["Machine"] = Environment.MachineName; })

                    // Several options to use different serializers
                    .DefaultSerializer(new CustomSerializer())
                    .CustomNewtonsoftJsonSerialization(new JsonSerializerSettings())

                    // Customize messages or the containing envelope on the way out
                    .CustomizeOutgoingMessagesOfType<IMessageType>((env, msg) =>
                    {
                        msg.MachineName = Environment.MachineName;
                    })
                    ;
            }).StartAsync();
    }
}

public class CustomSerializer : IMessageSerializer
{
    public string ContentType { get; }

    public byte[] Write(Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotImplementedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotImplementedException();
    }
}

public interface IMessageType
{
    string MachineName { get; set; }
}