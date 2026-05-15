using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_using_handler_type_naming : IAsyncLifetime, IDisposable
{
    private IHost _host = null!;
    private IWolverineRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableDeadLettering()
                .EnableSystemEndpoints()
                .UseConventionalRouting(NamingSource.FromHandlerType);

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(PubsubHandlerTypeNamingHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    [Fact]
    public void listener_endpoint_should_be_named_after_handler_type()
    {
        var expectedName = typeof(PubsubHandlerTypeNamingHandler).ToMessageTypeName();

        _runtime.Endpoints.ActiveListeners().Any(x => x.Uri.ToString().Contains(expectedName))
            .ShouldBeTrue($"Expected active listener containing '{expectedName}' for handler type");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _host.Dispose();
    }
}

public record PubsubHandlerTypeNamingMessage;

public class PubsubHandlerTypeNamingHandler
{
    public void Handle(PubsubHandlerTypeNamingMessage message)
    {
    }
}
