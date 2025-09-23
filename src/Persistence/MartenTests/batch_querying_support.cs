using IntegrationTests;
using JasperFx.Resources;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace MartenTests;

public class batch_querying_support : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(o =>
                {
                    o.Connection(Servers.PostgresConnectionString);
                    o.DatabaseSchemaName = "batching";
                    o.DisableNpgsqlLogging = true;

                }).UseLightweightSessions().IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task try_batch_querying_end_to_end()
    {
        using var session = theHost.DocumentStore().LightweightSession();
        var doc1 = new Doc1();
        session.Store(doc1);
        
        var doc2 = new Doc2();
        session.Store(doc2);
        
        var doc3 = new Doc3{Id = Guid.NewGuid().ToString()};
        session.Store(doc3);

        await session.SaveChangesAsync();

        await theHost.InvokeAsync(new DoStuffWithDocs(doc1.Id, doc2.Id, doc3.Id));
    }

    [Fact]
    public async Task try_batch_querying_with_read_aggregate()
    {
        using var session = theHost.DocumentStore().LightweightSession();
        var doc1 = new Doc1();
        session.Store(doc1);
        
        var doc2 = new Doc2();
        session.Store(doc2);

        var streamId = Guid.NewGuid();
        session.Events.StartStream<LetterAggregate>(streamId, new AEvent(), new BEvent(), new BEvent(), new DEvent());

        await session.SaveChangesAsync();
        
        await theHost.InvokeAsync(new ReadAggregateWithDocs(doc1.Id, doc2.Id, streamId));
    }
}

public record DoStuffWithDocs(Guid Doc1Id, Guid Doc2Id, string Doc3Id);

public static class DoStuffWithDocsHandler
{
    public static void Handle(
        DoStuffWithDocs command,
        [Entity] Doc1 doc1,
        [Entity] Doc2 doc2,
        [Entity] Doc3 doc3
        
        )
    {
        doc1.ShouldNotBeNull();
        doc2.ShouldNotBeNull();
        doc3.ShouldNotBeNull();
    }
}

public record ReadAggregateWithDocs(Guid Doc1Id, Guid Doc2Id, Guid LetterAggregateId);

public static class ReadAggregateWithDocsHandler
{
    public static void Handle(
        ReadAggregateWithDocs message,
        [Entity] Doc1 doc1,
        [Entity] Doc2 doc2,
        [ReadAggregate] LetterAggregate letters
    )
    {
        doc1.ShouldNotBeNull();
        doc2.ShouldNotBeNull();
        letters.ShouldNotBeNull();
        
        letters.ACount.ShouldBe(1);
        letters.BCount.ShouldBe(2);
    }
}

public class Doc1
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Somebody";
}

public class Doc2
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Somebody";
}

public class Doc3
{
    public string Id { get; set; }
    public string Name { get; set; } = "Somebody";
}

