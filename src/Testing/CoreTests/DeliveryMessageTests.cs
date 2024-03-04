using CoreTests.Persistence.Sagas;
using JasperFx.Core.Reflection;
using NSubstitute;
using TestingSupport.Compliance;
using Xunit;

namespace CoreTests;

public class DeliveryMessageTests
{
    [Fact]
    public async Task send_with_delivery_options()
    {
        var context = Substitute.For<IMessageContext>();

        var inner = new Message1();
        var message = inner.WithDeliveryOptions(new DeliveryOptions());

        await message.As<ISendMyself>().ApplyAsync(context);

        await context.Received().PublishAsync(inner, message.Options);
    }
}

public class RoutedToEndpointMessageTests
{
    [Fact]
    public async Task send_to_endpoint_by_name()
    {
        var context = Substitute.For<IMessageContext>();
        var endpoint = Substitute.For<IDestinationEndpoint>();
        context.EndpointFor("foo").Returns(endpoint);

        var inner = new Message1();
        var options = new DeliveryOptions();
        var message = inner.ToEndpoint("foo", options);

        await message.As<ISendMyself>().ApplyAsync(context);

        await endpoint.Received().SendAsync(inner, options);
    }
    
    [Fact]
    public async Task send_to_endpoint_by_uri()
    {
        var destination = new Uri("tcp://localhost:5000");
        
        var context = Substitute.For<IMessageContext>();
        var endpoint = Substitute.For<IDestinationEndpoint>();
        context.EndpointFor(destination).Returns(endpoint);

        var inner = new Message1();
        var options = new DeliveryOptions();
        var message = inner.ToDestination(destination, options);

        await message.As<ISendMyself>().ApplyAsync(context);

        await endpoint.Received().SendAsync(inner, options);
    }
}