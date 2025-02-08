using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;

namespace MartenTests;

public class batch_processing
{

    [Fact]
    public async Task end_to_end_with_durable()
    {
        using var theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.DatabaseSchemaName = "items";
                    m.Connection(Servers.PostgresConnectionString);
                }).IntegrateWithWolverine();
                
                opts.Policies.AutoApplyTransactions();
                
                opts.BatchMessagesOf<BatchItem>(batching =>
                {
                    // Purposely making this slower to let the test be more reliable
                    batching.TriggerTime = 1.Seconds();
                    batching.BatchSize = 8;
                    batching.LocalExecutionQueueName = "items";
                }).UseDurableInbox();
            }).StartAsync();
        
        await theHost.CleanAllMartenDataAsync();
        await theHost.ResetResourceState();
        
        var item1 = new BatchItem("one", Guid.NewGuid());
        var item2 = new BatchItem("two", Guid.NewGuid());
        var item3 = new BatchItem("three", Guid.NewGuid());
        var item4 = new BatchItem("four", Guid.NewGuid());
        var item5 = new BatchItem("five", Guid.NewGuid());
        var item6 = new BatchItem("six", Guid.NewGuid());
        var item7 = new BatchItem("seven", Guid.NewGuid());
        var item8 = new BatchItem("eight", Guid.NewGuid());

        Func<IMessageContext, Task> publish = async c =>
        {
            await c.PublishAsync(item1);
            await c.PublishAsync(item2);
            await c.PublishAsync(item3);
            await c.PublishAsync(item4);
            await c.PublishAsync(item5);
            await c.PublishAsync(item6);
            await c.PublishAsync(item7);
            await c.PublishAsync(item8);
        };
        
        var tracked = await theHost.TrackActivity()
            .WaitForMessageToBeReceivedAt<BatchItem[]>(theHost)
            .ExecuteAndWaitAsync(publish);

        var messages = tracked.Executed.MessagesOf<BatchItem[]>();

        var items = new List<BatchItem>();
        foreach (var message in messages)
        {
            items.AddRange(message);
        }

        items.Count.ShouldBe(8);
        items.ShouldContain(item1);
        items.ShouldContain(item2);
        items.ShouldContain(item3);
        items.ShouldContain(item4);
        items.ShouldContain(item5);
        items.ShouldContain(item6);
        items.ShouldContain(item7);
        items.ShouldContain(item8);

        using var session = theHost.DocumentStore().LightweightSession();
        var count = await session.Query<BatchItem>().CountAsync();
        
        count.ShouldBe(8);

        var incoming = await theHost.GetRuntime().Storage.Admin.AllIncomingAsync();
        foreach (var envelope in incoming)
        {
            envelope.Status.ShouldBe(EnvelopeStatus.Handled);
        }
    }

    [Fact]
    public async Task end_to_end_with_tenancy()
    {
        using var theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Policies.AllDocumentsAreMultiTenanted();
                    m.DisableNpgsqlLogging = true;
                    m.DatabaseSchemaName = "items";
                    m.Connection(Servers.PostgresConnectionString);
                }).IntegrateWithWolverine();
                
                opts.Policies.AutoApplyTransactions();
                
                opts.BatchMessagesOf<BatchItem>(batching =>
                {
                    // Purposely making this slower to let the test be more reliable
                    batching.TriggerTime = 1.Seconds();
                    batching.BatchSize = 8;
                    batching.LocalExecutionQueueName = "items";
                }).UseDurableInbox();
            }).StartAsync();
        
        var item1 = new BatchItem("one", Guid.NewGuid());
        var item2 = new BatchItem("two", Guid.NewGuid());
        var item3 = new BatchItem("three", Guid.NewGuid());
        var item4 = new BatchItem("four", Guid.NewGuid());
        var item5 = new BatchItem("five", Guid.NewGuid());
        var item6 = new BatchItem("six", Guid.NewGuid());
        var item7 = new BatchItem("seven", Guid.NewGuid());
        var item8 = new BatchItem("eight", Guid.NewGuid());

        await theHost.CleanAllMartenDataAsync();
        await theHost.ResetResourceState();
        
        Func<IMessageContext, Task> publish = async c =>
        {
            await c.PublishAsync(item1, new DeliveryOptions{TenantId = "blue"});
            await c.PublishAsync(item2, new DeliveryOptions{TenantId = "blue"});
            await c.PublishAsync(item3, new DeliveryOptions{TenantId = "green"});
            await c.PublishAsync(item4, new DeliveryOptions{TenantId = "green"});
            await c.PublishAsync(item5, new DeliveryOptions{TenantId = "blue"});
            await c.PublishAsync(item6, new DeliveryOptions{TenantId = "green"});
            await c.PublishAsync(item7, new DeliveryOptions{TenantId = "blue"});
            await c.PublishAsync(item8, new DeliveryOptions{TenantId = "blue"});
        };
        
        var tracked = await theHost.TrackActivity()
            .WaitForCondition(new AllItemsReceived(item1, item2, item3, item4, item5, item6, item7, item8))
            .ExecuteAndWaitAsync(publish);

        var messages = tracked.Executed.Envelopes().Where(x => x.Message is BatchItem[]).ToArray();

        var items = new List<BatchItem>();
        foreach (var message in messages.Select(x => x.Message).OfType<BatchItem[]>())
        {
            items.AddRange(message);
        }

        items.Count.ShouldBe(8);
        items.ShouldContain(item1);
        items.ShouldContain(item2);
        items.ShouldContain(item3);
        items.ShouldContain(item4);
        items.ShouldContain(item5);
        items.ShouldContain(item6);
        items.ShouldContain(item7);
        items.ShouldContain(item8);

        using var blue = theHost.DocumentStore().LightweightSession("blue");
        var blueItems = await blue.Query<BatchItem>().ToListAsync();
        blueItems.Count.ShouldBe(5);
        
        using var green = theHost.DocumentStore().LightweightSession("green");
        var greenItems = await green.Query<BatchItem>().ToListAsync();
        greenItems.Count.ShouldBe(3);
    }
}

public class AllItemsReceived(params BatchItem[] Items) : ITrackedCondition
{
    private readonly List<BatchItem> _received = new();
    
    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType == MessageEventType.MessageSucceeded && record.Message is BatchItem[] items)
        {
            _received.AddRange(items);
        }
    }

    public bool IsCompleted()
    {
        return Items.All(x => _received.Any(r => r.Id == x.Id));
    }
}

public record NoItem(string Name);

public record BatchItem(string Name, Guid Id);

public static class BatchItemHandler
{
    public static void Handle(BatchItem[] items, IDocumentSession session)
    {
        foreach (var item in items)
        {
            session.Store(item);
        }
    }
}