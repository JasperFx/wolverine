using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

[Collection("mysql")]
public class end_to_end_from_scratch : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var connectionString = Servers.MySqlConnectionString;

        Api = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.ServiceName = "api";
                options.Durability.Mode = DurabilityMode.Solo;
                options.DefaultExecutionTimeout = TimeSpan.FromSeconds(90);

                options.UseMySqlPersistenceAndTransport(connectionString, schema: "api", transportSchema: "integration")
                    .AutoProvision()
                    .AutoPurgeOnStartup(); //May not want this in a real environment, but useful while developing
                options.PublishAllMessages().ToMySqlQueue("ui");
                options.ListenToMySqlQueue("api");
            }).StartAsync();

        UI = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.ServiceName = "ui";
                options.Durability.Mode = DurabilityMode.Solo;
                options.DefaultExecutionTimeout = TimeSpan.FromSeconds(90);

                options.UseMySqlPersistenceAndTransport(connectionString, "ui", transportSchema: "integration")
                    .AutoProvision();
                options.PublishAllMessages().ToMySqlQueue("api");
                options.ListenToMySqlQueue("ui");
            }).StartAsync();
    }

    public IHost Api { get; private set; } = null!;
    public IHost UI { get; private set; } = null!;

    public async Task DisposeAsync()
    {
        await Api.StopAsync();
        await UI.StopAsync();
    }

    [Fact]
    public async Task can_send_messages_end_to_end()
    {
        var session = await UI
            .TrackActivity()
            .AlsoTrack(Api)
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new MySqlApiRequest("Rey"));

        // It's handled in UI
        session.Received.RecordsInOrder().Single(x => x.Message is MySqlApiRequest)
            .ServiceName.ShouldBe("api");

        // UI handles the response
        session.MessageSucceeded.RecordsInOrder()
            .Single(x => x.Message is MySqlUIResponse)
            .ServiceName.ShouldBe("ui");
    }
}

public record MySqlApiRequest(string Name);
public record MySqlUIResponse(string Name);

public static class MySqlApiHandler
{
    public static MySqlUIResponse Handle(MySqlApiRequest request)
        => new MySqlUIResponse(request.Name);
}

public static class MySqlUIHandler
{
    public static void Handle(MySqlUIResponse response) => Debug.WriteLine("Got a ui response");
}
