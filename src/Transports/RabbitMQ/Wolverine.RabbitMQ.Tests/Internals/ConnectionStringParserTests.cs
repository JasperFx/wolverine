using JasperFx.Core;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class ConnectionStringParserTests
{
    private readonly ConnectionFactory theFactory = new();

    [Fact]
    public void parse_host_happy_path()
    {
        ConnectionStringParser.Apply("host=foo", theFactory);
        theFactory.HostName.ShouldBe("foo");

        ConnectionStringParser.Apply("Host=bar", theFactory);
        theFactory.HostName.ShouldBe("bar");
    }

    [Fact]
    public void parse_port_happy_path()
    {
        ConnectionStringParser.Apply("Port=5673", theFactory);
        theFactory.Port.ShouldBe(5673);
    }

    [Fact]
    public void parse_port_sad_path()
    {
        var message = Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            ConnectionStringParser.Apply("Port=junk", theFactory);
        });

        message.Message.ShouldContain("Supplied port 'junk' is an invalid number");
    }

    [Fact]
    public void parses_username_and_password()
    {
        ConnectionStringParser.Apply("username=foo;password=bar", theFactory);
        theFactory.UserName.ShouldBe("foo");
        theFactory.Password.ShouldBe("bar");
    }

    [Fact]
    public void requested_heartbeat_happy_path()
    {
        ConnectionStringParser.Apply("requestedheartbeat=33", theFactory);
        theFactory.RequestedHeartbeat.ShouldBe(33.Seconds());
    }

    [Fact]
    public void virtual_host()
    {
        ConnectionStringParser.Apply("virtualhost=weird", theFactory);
        theFactory.VirtualHost.ShouldBe("weird");
    }
}