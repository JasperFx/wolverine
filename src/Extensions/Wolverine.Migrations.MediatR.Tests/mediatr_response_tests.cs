using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Alba;

namespace Wolverine.Migrations.MediatR.Tests;

public class mediatr_response_tests
{
    private IHostBuilder _builder;
    public mediatr_response_tests()
    {
        _builder = Host.CreateDefaultBuilder();
        _builder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(mediatr_cascading_message_tests).Assembly);
            opts.MigrateFromMediatR(typeof(RequestCascadeHandler).Assembly);
        });
    }

    [Fact]
    public async Task response_is_returned_from_invoke_async()
    {
        await using var host = await AlbaHost.For(_builder);

        var response = await host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("response-test"));

        response.ShouldNotBeNull();
        response.Data.ShouldBe("passed: response-test");
    }

    [Fact]
    public async Task response_type_is_correct()
    {
        await using var host = await AlbaHost.For(_builder);

        var response = await host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("type-test"));

        response.ShouldBeOfType<Response>();
    }
}
