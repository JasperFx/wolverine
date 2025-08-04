using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class bug_369_reply_to_local_message_tries_to_be_Outgoing : PostgresqlContext
{
    [Fact]
    public async Task why_are_we_going_as_outgoing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        var tracked = await host.SendMessageAndWaitAsync(new Ping());

        tracked.Executed.SingleEnvelope<Pong>().Status.ShouldBe(EnvelopeStatus.Incoming);
    }
}

public record Ping();

public static class PingHandler
{
    public static async Task<Pong> Handle(Ping ping)
    {
        //await Task.Delay(TimeSpan.FromSeconds(10));
        return new Pong();
    }
}

public record Pong();
public static class PongHandler
{
    public static async Task Handle(Pong pong)
    {
        //await Task.Delay(TimeSpan.FromSeconds(10));
        Console.WriteLine("Done!");
    }
}