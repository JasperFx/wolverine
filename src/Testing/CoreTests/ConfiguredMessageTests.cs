using CoreTests.Persistence.Sagas;
using JasperFx.Core.Reflection;
using NSubstitute;
using Xunit;

namespace CoreTests;

public class ConfiguredMessageTests
{
    [Fact]
    public async Task send_with_delivery_options()
    {
        var context = Substitute.For<IMessageContext>();

        var inner = new Message1();
        var message = new ConfiguredMessage(inner, new DeliveryOptions());

        await message.As<ISendMyself>().ApplyAsync(context);

        await context.Received().PublishAsync(inner, message.Options);
    }
}