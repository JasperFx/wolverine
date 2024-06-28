using CoreTests.Acceptance;
using CoreTests.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using SimpleBeforeAndAfter = CoreTests.Configuration.SimpleBeforeAndAfter;

namespace CoreTests.Bugs;

public class Bug_42_concurrent_creation_of_command_handlers
{
    [Fact]
    public async Task try_to_break()
    {
        for (var i = 0; i < 10; i++)
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.Policies.AddMiddleware<SimpleBeforeAndAfter>();
                opts.Policies.AddMiddleware<SimpleBeforeAndAfterAsync>();
            }).StartAsync();

            var bus = host.MessageBus();

            var commands = Enumerable.Range(1, 100)
                .Select(i => i % 2 == 0 ? new OtherTracedMessage() : (object)new TracedMessage());

            await Parallel.ForEachAsync(commands, CancellationToken.None,
                async (c, token) => await bus.InvokeAsync(c, token));
        }
    }
}