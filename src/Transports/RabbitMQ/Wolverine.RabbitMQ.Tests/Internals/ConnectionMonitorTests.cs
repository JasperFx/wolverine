using System.Reflection;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class ConnectionMonitorTests
{
    [Fact]
    public async Task default_channel_options_do_not_enable_publisher_confirmations()
    {
        var transport = new RabbitMqTransport();
        var monitor = new ConnectionMonitor(transport, ConnectionRole.Sending);

        var connection = Substitute.For<IConnection>();
        var channel = Substitute.For<IChannel>();
        CreateChannelOptions? capturedOptions = null;

        connection.CreateChannelAsync(Arg.Do<CreateChannelOptions>(o => capturedOptions = o))
            .Returns(Task.FromResult(channel));

        SetConnection(monitor, connection);

        await monitor.CreateChannelAsync();

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PublisherConfirmationsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task configured_channel_options_enable_publisher_confirmations()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureChannelOptions(options => options with
        {
            PublisherConfirmationsEnabled = true
        });

        var monitor = new ConnectionMonitor(transport, ConnectionRole.Sending);

        var connection = Substitute.For<IConnection>();
        var channel = Substitute.For<IChannel>();
        CreateChannelOptions? capturedOptions = null;

        connection.CreateChannelAsync(Arg.Do<CreateChannelOptions>(o => capturedOptions = o))
            .Returns(Task.FromResult(channel));

        SetConnection(monitor, connection);

        await monitor.CreateChannelAsync();

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PublisherConfirmationsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task tenant_transport_inherits_channel_options()
    {
        var parent = new RabbitMqTransport();
        parent.ConfigureFactory(f => f.HostName = "localhost");
        parent.ConfigureChannelOptions(options => options with
        {
            PublisherConfirmationsEnabled = true
        });

        var tenant = new RabbitMqTenant("tenant", "virtual");
        tenant.Compile(parent);

        var monitor = new ConnectionMonitor(tenant.Transport, ConnectionRole.Sending);

        var connection = Substitute.For<IConnection>();
        var channel = Substitute.For<IChannel>();
        CreateChannelOptions? capturedOptions = null;

        connection.CreateChannelAsync(Arg.Do<CreateChannelOptions>(o => capturedOptions = o))
            .Returns(Task.FromResult(channel));

        SetConnection(monitor, connection);

        await monitor.CreateChannelAsync();

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PublisherConfirmationsEnabled.ShouldBeTrue();
    }

    private static void SetConnection(ConnectionMonitor monitor, IConnection connection)
    {
        var field = typeof(ConnectionMonitor).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(monitor, connection);
    }
}
