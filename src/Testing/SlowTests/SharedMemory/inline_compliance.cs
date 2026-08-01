using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace SlowTests.SharedMemory;

public class InlineSharedMemoryInlineFixture : TransportComplianceFixture, IAsyncLifetime
{
    public InlineSharedMemoryInlineFixture() : base(new Uri("shared-memory://receiver"), 5)
    {
        AllLocally = true;
    }

    public async ValueTask InitializeAsync()
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

    // AfterDisposeAsync, not a `new DisposeAsync`: TransportCompliance<T> disposes the fixture through
    // the statically-bound base method, so a hiding override never runs and the queues were never cleared.
    // See #3763.
    protected override Task AfterDisposeAsync()
    {
        return SharedMemoryQueueManager.ClearAllAsync();
    }
}

public class inline_compliance : TransportCompliance<InlineSharedMemoryInlineFixture>
{
    
}