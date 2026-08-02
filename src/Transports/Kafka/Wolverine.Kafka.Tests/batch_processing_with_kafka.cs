using Alba;
using JasperFx.Core;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class batch_processing_with_kafka
{
    [Fact]
    public async Task end_to_end()
    {
        await using var host = await AlbaHost.For<Program>(_ => {});

        IScenarioResult result = null!;

        Func<IMessageContext, Task> execute = async _ =>
        {
            result = await host.Scenario(x => { x.Post.Url("/test"); });
        };
        
        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<TestMessage[]>(host)
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(execute);

        // Both published messages must arrive at the handler in batch (TestMessage[]) form. Deliberately
        // NOT asserting a single batch of two: BatchingOptions triggers on a full batch OR on TriggerTime
        // (250ms by default), so whether two messages published back-to-back land in one batch or in two
        // depends on how the Kafka consumer happens to slice its polls. Asserting the grouping asserts a
        // race — the contract that actually matters is that every message was delivered, batched.
        var batched = tracked.Received.MessagesOf<TestMessage[]>().ToArray();

        batched.ShouldNotBeEmpty();
        batched.Sum(x => x.Length).ShouldBe(2);
    }
}