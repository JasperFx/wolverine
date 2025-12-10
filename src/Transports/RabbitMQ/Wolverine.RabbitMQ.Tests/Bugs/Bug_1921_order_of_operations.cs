using Microsoft.Extensions.Hosting;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_1921_order_of_operations
{
    [Fact]
    public async Task start_up_should_succeed()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    //.AutoProvision()
                    .EnableWolverineControlQueues()
                    .CustomizeDeadLetterQueueing(
                        new($"my-awesome-dead-letter-queue", DeadLetterQueueMode.Native)
                    );
            }).StartAsync();
    }
}