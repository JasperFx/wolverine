using Alba;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class batch_processing_with_kafka
{
    [Fact]
    public async Task end_to_end()
    {
        JasperFxEnvironment.AutoStartHost = true;
        
        await using var host = await AlbaHost.For<Program>(_ => {});

        IScenarioResult result = null!;

        Func<IMessageContext, Task> execute = async _ =>
        {
            result = await host.Scenario(x => { x.Post.Url("/test"); });
        };
        
        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<TestMessage[]>(host)
            .ExecuteAndWaitAsync(execute);

        tracked.FindSingleTrackedMessageOfType<TestMessage[]>()
            .Length.ShouldBe(2);
    }
}