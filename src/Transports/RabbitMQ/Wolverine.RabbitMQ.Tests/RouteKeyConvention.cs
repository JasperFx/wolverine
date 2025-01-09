using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.RabbitMQ.Tests;

#region sample_RouteKeyConvention

public class RouteKeyConvention : IMessageRoutingConvention
{
    private readonly string _exchangeName;

    public RouteKeyConvention(string exchangeName)
    {
        _exchangeName = exchangeName;
    }

    public void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        // Not worrying about this at all for this case
    }

    public IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        var routingKey = messageType.FullNameInCode().ToLowerInvariant();
        var rabbitTransport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();

        // Find or create the named Rabbit MQ exchange in Wolverine's model
        var exchange = rabbitTransport.Exchanges[_exchangeName];
        
        // Find or create the named routing key / binding key
        // in Wolverine's model
        var routing = exchange.Routings[routingKey];

        // Tell Wolverine you want the message type routed to this
        // endpoint
        yield return routing;
    }
}

#endregion

public static class UsingRouteKeyConvention
{
    public static async Task use_it()
    {
        #region sample_register_RouteKeyConvention

        var builder = Host.CreateApplicationBuilder();
        var rabbitConnectionString = builder
            .Configuration
            .GetConnectionString("rabbitmq");

        builder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbitConnectionString)
                .AutoProvision();

            var exchangeName = builder
                .Configuration
                .GetValue<string>("exchange-name");

            opts.RouteWith(new RouteKeyConvention(exchangeName));
        });

        // actually start the app...

        #endregion
    }
}