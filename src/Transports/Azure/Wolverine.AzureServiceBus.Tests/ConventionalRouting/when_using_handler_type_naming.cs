using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

public class when_using_handler_type_naming : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseConventionalRouting(NamingSource.FromHandlerType)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AsbHandlerTypeNamingHandler));
            }).Start();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        return Task.CompletedTask;
    }

    [Fact]
    public void listener_endpoint_should_be_named_after_handler_type()
    {
        var expectedName = typeof(AsbHandlerTypeNamingHandler).ToMessageTypeName();
        var transport = _runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        transport.Queues.Any(q => q.QueueName == expectedName)
            .ShouldBeTrue($"Expected queue named '{expectedName}' for handler type");
    }

    [Fact]
    public void listener_should_be_active()
    {
        var expectedName = typeof(AsbHandlerTypeNamingHandler).ToMessageTypeName();

        _runtime.Endpoints.ActiveListeners().Any(x => x.Uri.ToString().Contains(expectedName))
            .ShouldBeTrue($"Expected active listener containing '{expectedName}'");
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
    }
}

public record AsbHandlerTypeNamingMessage;

public class AsbHandlerTypeNamingHandler
{
    public void Handle(AsbHandlerTypeNamingMessage message)
    {
    }
}
