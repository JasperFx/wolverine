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
public class using_storage_return_types_and_entity_attributes
{
    private readonly AppFixture _fixture;

    public using_storage_return_types_and_entity_attributes(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task can_use_cosmosdb_ops_as_side_effects()
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

        var tracked = await host.InvokeMessageAndWaitAsync(new CreateDocument("doc1", "Test Document"));
        tracked.Executed.MessagesOf<CreateDocument>().Any().ShouldBeTrue();
    }
}

public record CreateDocument(string Id, string Name);

public static class CreateDocumentHandler
{
    public static ICosmosDbOp Handle(CreateDocument command)
    {
        var doc = new TestDocument { id = command.Id, name = command.Name, partitionKey = "test" };
        return CosmosDbOps.Store(doc);
    }
}

public class TestDocument
{
    public string id { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string partitionKey { get; set; } = string.Empty;
}
