using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class when_using_handler_type_naming : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime _runtime;

    public when_using_handler_type_naming()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq()
                .UseConventionalRouting(NamingSource.FromHandlerType)
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(HandlerTypeNamingHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    [Fact]
    public void listener_endpoint_should_be_named_after_handler_type()
    {
        var expectedName = typeof(HandlerTypeNamingHandler).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();
        transport.Queues.Contains(expectedName).ShouldBeTrue(
            $"Expected queue named '{expectedName}' for handler type, but found queues: {string.Join(", ", transport.Queues.Select(q => q.EndpointName))}");
    }

    [Fact]
    public void listener_should_not_be_named_after_message_type()
    {
        var messageName = typeof(HandlerTypeNamingMessage).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();

        // The message type name should NOT be used for the listener queue
        // (it may still exist as an exchange for sending)
        var queue = transport.Queues.FirstOrDefault(q => q.EndpointName == messageName);
        if (queue != null)
        {
            queue.IsListener.ShouldBeFalse(
                $"Queue '{messageName}' should not be a listener when using FromHandlerType naming");
        }
    }

    [Fact]
    public void listener_endpoint_should_be_active()
    {
        var expectedName = typeof(HandlerTypeNamingHandler).ToMessageTypeName();
        var expectedUri = $"rabbitmq://queue/{expectedName}".ToUri();

        _runtime.Endpoints.ActiveListeners().Any(x => x.Uri == expectedUri)
            .ShouldBeTrue($"Expected active listener at {expectedUri}");
    }

    public void Dispose()
    {
        _host.Dispose();
    }
}

public record HandlerTypeNamingMessage;

public class HandlerTypeNamingHandler
{
    public void Handle(HandlerTypeNamingMessage message)
    {
    }
}
