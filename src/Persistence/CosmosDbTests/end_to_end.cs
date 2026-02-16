using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Tracking;

namespace CosmosDbTests;

[Collection("cosmosdb")]
public class end_to_end
{
    private readonly AppFixture _fixture;

    public end_to_end(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task can_send_and_receive_messages()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
                opts.Discovery.IncludeAssembly(GetType().Assembly);
            }).StartAsync();

        var tracked = await host.InvokeMessageAndWaitAsync(new SmokeTestMessage("Hello, CosmosDb!"));

        tracked.Executed.MessagesOf<SmokeTestMessage>().Any().ShouldBeTrue();
    }
}

public record SmokeTestMessage(string Text);

public static class SmokeTestMessageHandler
{
    public static void Handle(SmokeTestMessage message)
    {
        // no-op
    }
}
