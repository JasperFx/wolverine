using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;
using Alba;

namespace Wolverine.Migrations.MediatR.Tests;

public class mediatr_cascading_message_tests
{
    private IHostBuilder _builder;
    public mediatr_cascading_message_tests()
    {
        _builder = Host.CreateDefaultBuilder();
        _builder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(mediatr_cascading_message_tests).Assembly);
            opts.MigrateFromMediatR(typeof(RequestCascadeHandler).Assembly);
        });
    }

    [Fact]
    public async Task mediatr_handler_can_return_cascading_message()
    {
        await using var host = await AlbaHost.For(_builder);

        // Reset static state
        CascadingMessageHandler.ReceivedData = null;

        // Invoke the message and wait for cascading messages to be processed
        var tracked = await host.InvokeMessageAndWaitAsync(new CascadingMessage("cascade-test"));

        // Verify the cascading message was handled
        CascadingMessageHandler.ReceivedData.ShouldBe("cascade-test");
    }
}
