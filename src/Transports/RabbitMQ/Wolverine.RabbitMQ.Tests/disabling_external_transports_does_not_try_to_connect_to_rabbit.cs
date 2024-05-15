using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class disabling_external_transports_does_not_try_to_connect_to_rabbit
{
    [Fact]
    public async Task can_execute_locally()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();

                // This could never, ever work
                opts.UseRabbitMq(x => x.HostName = Guid.NewGuid().ToString());
                opts.PublishMessage<SayName>().ToRabbitQueue("name");

                opts.StubAllExternalTransports();
            }).StartAsync();

        var session = await host.SendMessageAndWaitAsync(new SayName("Jennifer Coolidge"));

        session.Sent.SingleEnvelope<SayName>()
            .Destination.ShouldBe(new Uri("rabbitmq://queue/name"));
    }

    public record SayName(string Name);

    public static class SayNameHandler
    {
        public static void Handle(SayName name)
        {
            Console.WriteLine("My name is " + name);
        }
    }
}