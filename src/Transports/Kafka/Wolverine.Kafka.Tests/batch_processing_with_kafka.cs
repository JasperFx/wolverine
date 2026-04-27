using Alba;
using JasperFx.Core;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

[Trait("Category", "Flaky")]
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

        tracked.FindSingleTrackedMessageOfType<TestMessage[]>()
            .Length.ShouldBe(2);
    }
}