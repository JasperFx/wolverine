using System.Globalization;
using System.Reflection;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

/// <summary>
/// Under tr-TR an upper case "I" lower cases to the dotless "ı" (U+0131), so names and connection string keys
/// that are lower cased for the broker or for a lookup have to use the invariant culture.
/// </summary>
public class culture_invariant_naming
{
    private static async Task UnderTurkishCultureAsync(Func<Task> action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task exchange_check_passively_declares_the_invariant_lower_case_name()
    {
        var channel = Substitute.For<IChannel>();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns(channel);

        var transport = new RabbitMqTransport();
        var monitor = new ConnectionMonitor(transport, ConnectionRole.Listening);
        typeof(ConnectionMonitor).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(monitor, connection);
        typeof(RabbitMqTransport).GetField("_listenerConnection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(transport, monitor);

        var exchange = new RabbitMqExchange("Invoices-Exchange", transport);

        bool found = false;
        await UnderTurkishCultureAsync(async () => found = await exchange.CheckAsync());

        found.ShouldBeTrue();
        await channel.Received().ExchangeDeclarePassiveAsync("invoices-exchange", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task connection_string_keys_are_matched_culture_invariantly()
    {
        var factory = new ConnectionFactory();

        await UnderTurkishCultureAsync(() =>
        {
            ConnectionStringParser.Apply("VIRTUALHOST=weird", factory);
            return Task.CompletedTask;
        });

        factory.VirtualHost.ShouldBe("weird");
    }
}
