using System.Diagnostics;
using IntegrationTests;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class end_to_end_from_scratch : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var connectionString = Servers.PostgresConnectionString;
        
        Api = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.ServiceName = "api";
                options.Durability.Mode = DurabilityMode.Solo;
                options.DefaultExecutionTimeout = TimeSpan.FromSeconds(90);
                
                options.UsePostgresqlPersistenceAndTransport(connectionString, schema:"api", transportSchema: "integration")
                    .AutoProvision()
                    .AutoPurgeOnStartup(); //May not want this in a real environment, but useful while developing
                options.PublishAllMessages().ToPostgresqlQueue("ui");
                options.ListenToPostgresqlQueue("api");
            }).StartAsync();

        UI = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.ServiceName = "ui";
                options.Durability.Mode = DurabilityMode.Solo;
                options.DefaultExecutionTimeout = TimeSpan.FromSeconds(90);
                
                options.UsePostgresqlPersistenceAndTransport(connectionString, "ui", transportSchema: "integration")
                    .AutoProvision();
                options.PublishAllMessages().ToPostgresqlQueue("api");
                options.ListenToPostgresqlQueue("ui");
            }).StartAsync();
    }

    public IHost Api { get; private set;}
    public IHost UI { get; private set;}

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
            .PublishMessageAndWaitAsync(new ApiRequest("Rey"));
        
        // It's handled in UI
        session.Received.RecordsInOrder().Single(x => x.Message is ApiRequest)
            .ServiceName.ShouldBe("api");
        
        // UI handles the response
        session.MessageSucceeded.RecordsInOrder()
            .Single(x => x.Message is UIResponse)
            .ServiceName.ShouldBe("ui");
            
            
    }
}

public record ApiRequest(string Name);
public record UIResponse(string Name);

public static class ApiHandler
{
    public static UIResponse Handle(ApiRequest request) 
        => new UIResponse(request.Name);

}

public static class UIHandler
{
    public static void Handle(UIResponse response) => Debug.WriteLine("Got a ui response");
}