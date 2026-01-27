using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace SlowTests.SharedMemory;

public class BufferedSharedMemoryInlineFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedSharedMemoryInlineFixture() : base(new Uri("shared-memory://receiver"), 5)
    {
        AllLocally = true;
    }

    public async Task InitializeAsync()
    {
        await SharedMemoryQueueManager.ClearAllAsync();
        
        await ReceiverIs(opts =>
        {
            opts.ListenToSharedMemorySubscription("receiver", "receiver");
        });

        await SenderIs(opts =>
        {
            opts.ListenToSharedMemorySubscription("sender", "sender");
            opts.PublishAllMessages().ToSharedMemoryTopic("receiver");
        });
    }

    public Task DisposeAsync()
    {
        return SharedMemoryQueueManager.ClearAllAsync();
    }
}

public class buffered_compliance : TransportCompliance<BufferedSharedMemoryInlineFixture>
{
    
}