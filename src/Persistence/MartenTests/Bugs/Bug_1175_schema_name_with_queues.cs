using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_1175_schema_name_with_queues
{
    [Fact]
    public async Task send_messages_with_postgresql_queueing()
    {
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Service";

                opts.ListenToPostgresqlQueue("response").MaximumParallelMessages(14, ProcessingOrder.UnOrdered);
                opts.PublishMessage<ColorRequest>().ToPostgresqlQueue("request");

                opts.Services.AddMarten(opt =>
                    {
                        opt.Connection(Servers.PostgresConnectionString);
                        opt.Events.TenancyStyle = TenancyStyle.Conjoined;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine(options =>
                    {
                        options.AutoCreate = AutoCreate.CreateOrUpdate;
                        options.MessageStorageSchemaName = "sender";
                    });

                opts.Services.AddResourceSetupOnStartup();

            }).StartAsync();
        
        using var listener = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Listener";

                opts.ListenToPostgresqlQueue("request").MaximumParallelMessages(14, ProcessingOrder.UnOrdered);
                opts.PublishMessage<ColorResponse>().ToPostgresqlQueue("response");

                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine(options =>
                    {
                        options.AutoCreate = AutoCreate.CreateOrUpdate;
                        options.MessageStorageSchemaName = "listener";
                    });

                opts.Services.AddResourceSetupOnStartup();

            }).StartAsync();

        var tracked = await sender.TrackActivity().AlsoTrack(listener).SendMessageAndWaitAsync(new ColorRequest("red"));
        tracked.Received.SingleMessage<ColorResponse>().Color.ShouldBe("red");
        tracked.Received.SingleEnvelope<ColorResponse>()
            .Destination.ShouldBe(new Uri("postgresql://response/"));

    }
}

public record ColorRequest(string Color);
public record ColorResponse(string Color);

public static class ColorRequestHandler
{
    public static async Task<ColorResponse> Handle(ColorRequest request)
    {
        await Task.Delay(Random.Shared.Next(0, 500).Milliseconds());
        return new ColorResponse(request.Color);
    }
}

public static class ColorResponseHandler
{
    public static void Handle(ColorResponse response) => Debug.WriteLine("Got color response for " + response.Color);
}
