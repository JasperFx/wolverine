using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Sharding;
using Xunit;

namespace CoreTests.Runtime.Sharding;

public class ShardedExecutionBlockSmokeTests
{
    [Fact]
    public async Task do_not_blow_up()
    {
        var count = 0;

        var grouping = new MessagePartitioningRules(new());
        grouping.ByMessage<ICoffee>(x => x.Name);

        var block = new ShardedExecutionBlock(5, grouping, (e, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        Task[] tasks = new Task[5];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 1000; j++)
                {
                    var envelope = ObjectMother.Envelope();
                    envelope.Message = new Coffee2(Guid.NewGuid().ToString());
                    await block.PostAsync(envelope);
                }
            });
        }

        await Task.WhenAll(tasks);

        await block.WaitForCompletionAsync();
        
        count.ShouldBe(5000);
    }
}