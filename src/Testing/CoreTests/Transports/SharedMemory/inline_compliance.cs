using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Transports.SharedMemory;

public class InlineSharedMemoryInlineFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineSharedMemoryInlineFixture() : base(new Uri("shared-memory://receiver"), 5)
    {
        AllLocally = true;
    }

    public async Task InitializeAsync()
    {
        await SharedMemoryQueueManager.ClearAllAsync();
        
        await ReceiverIs(opts =>
        {
            opts.ListenToSharedMemorySubscription("receiver", "receiver").ProcessInline();
        });

        await SenderIs(opts =>
        {
            opts.ListenToSharedMemorySubscription("sender", "sender");
            opts.PublishAllMessages().ToSharedMemoryTopic("receiver").SendInline();
        });
    }

    public Task DisposeAsync()
    {
        return SharedMemoryQueueManager.ClearAllAsync();
    }
}

public class inline_compliance : TransportCompliance<InlineSharedMemoryInlineFixture>
{
    
}