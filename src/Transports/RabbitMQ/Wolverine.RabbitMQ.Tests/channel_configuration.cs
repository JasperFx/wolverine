using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class channel_configuration
{
    public static async Task configure_sample()
    {
        #region sample_configuring_rabbit_mq_channel_creation

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts
                .UseRabbitMq(builder.Configuration.GetConnectionString("rabbitmq"))

                // Fine tune how the underlying Rabbit MQ channels from
                // this application will behave
                .ConfigureChannelCreation(o =>
                {
                    o.PublisherConfirmationsEnabled = true;
                    o.PublisherConfirmationTrackingEnabled = true;
                    o.ConsumerDispatchConcurrency = 5;
                });
        });

        #endregion
    }
    
    [Fact]
    public void can_customize_channel_creation()
    {
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.ConfigureChannelCreation(o =>
        {
            o.PublisherConfirmationsEnabled = true;
            o.PublisherConfirmationTrackingEnabled = true;
            o.ConsumerDispatchConcurrency = 5;
        });

        var wolverineOptions = new WolverineRabbitMqChannelOptions();
        transport.ChannelCreationOptions?.Invoke(wolverineOptions);
        
        wolverineOptions.PublisherConfirmationsEnabled.ShouldBe(true);
        wolverineOptions.PublisherConfirmationTrackingEnabled.ShouldBe(true);
        wolverineOptions.ConsumerDispatchConcurrency.ShouldBeEquivalentTo((ushort)5);
    }

    [Fact]
    public void can_customize_channel_creation_additively()
    {
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.ConfigureChannelCreation(o =>
        {
            o.PublisherConfirmationsEnabled = true;
        })
        .ConfigureChannelCreation(o =>
        {
            o.ConsumerDispatchConcurrency = 2;
        });

        var wolverineOptions = new WolverineRabbitMqChannelOptions();
        transport.ChannelCreationOptions?.Invoke(wolverineOptions);
        
        wolverineOptions.PublisherConfirmationsEnabled.ShouldBe(true);
        wolverineOptions.PublisherConfirmationTrackingEnabled.ShouldBe(false);
        wolverineOptions.ConsumerDispatchConcurrency.ShouldBeEquivalentTo((ushort)2);
    }
}