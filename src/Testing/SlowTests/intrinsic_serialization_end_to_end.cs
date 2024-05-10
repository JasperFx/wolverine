using System.Text;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace SlowTests;

public class intrinsic_serialization_end_to_end
{
    [Fact]
    public async Task send_message_between_nodes()
    {
        var port = PortFinder.GetAvailablePort();

        var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(port);
            }).StartAsync();

        var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(port);
            }).StartAsync();

        var tracked = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .SendMessageAndWaitAsync(new SerializedMessage { Name = "Travis Kelce" });

        tracked.Received.SingleMessage<SerializedMessage>()
            .Name.ShouldBe("Travis Kelce");
    }
}

public static class SerializedMessageHandler
{
    public static void Handle(SerializedMessage message)
    {
        Console.WriteLine(message.Name);
    }
}

public class SerializedMessage : ISerializable
{
    public string Name { get; set; } = "Bob Schneider";

    public byte[] Write()
    {
        return Encoding.Default.GetBytes(Name);
    }

    public static object Read(byte[] bytes)
    {
        var name = Encoding.Default.GetString(bytes);
        return new SerializedMessage { Name = name };
    }
}