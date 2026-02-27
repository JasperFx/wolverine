using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Alba;

namespace Wolverine.Shims.Tests.MediatR;

public class mediatr_shim_handler_integration_tests
{
    private IHostBuilder _builder;
    public mediatr_shim_handler_integration_tests()
    {
        _builder = Host.CreateDefaultBuilder();
        _builder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(mediatr_shim_cascading_message_tests).Assembly);
            opts.Services.AddScoped<IAdditionService, AdditionService>();
            opts.UseMediatRHandlers();
        });
    }

    [Fact]
    public async Task invoke_mediatr_handler_with_response()
    {
        await using var host = await AlbaHost.For(_builder);

        var response = await host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("test"));

        response.ShouldNotBeNull();
        response.Data.ShouldBe("passed: test");
        response.ProcessedBy.ShouldBe("MediatR");
    }

    [Fact]
    public async Task invoke_mediatr_handler_without_response()
    {
        await using var host = await AlbaHost.For(_builder);

        // Should not throw
        await host.InvokeAsync(new RequestWithoutResponse("test"));
    }

    [Fact]
    public async Task mediatr_handler_receives_dependencies_from_di()
    {
        await using var host = await AlbaHost.For(_builder);

        var response = await host.MessageBus().InvokeAsync<int>(
            new RequestAdditionFromService(1));

        response.ShouldBe(2);
    }
}
