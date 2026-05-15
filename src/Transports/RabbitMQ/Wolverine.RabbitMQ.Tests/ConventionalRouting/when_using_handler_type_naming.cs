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

public class when_using_handler_type_naming : IAsyncLifetime, IDisposable
{
    private IHost _host = null!;
    private IWolverineRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .UseConventionalRouting(NamingSource.FromHandlerType);

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(HandlerTypeNamingHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

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

    [Fact]
    public void exchange_should_be_named_after_message_type_not_handler_type()
    {
        var messageName = typeof(HandlerTypeNamingMessage).ToMessageTypeName();
        var handlerName = typeof(HandlerTypeNamingHandler).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();

        // The exchange should be named after the message type
        transport.Exchanges.Any(e => e.Name == messageName).ShouldBeTrue(
            $"Expected exchange named '{messageName}' for message type, but found exchanges: {string.Join(", ", transport.Exchanges.Select(e => e.Name))}");

        // The exchange should NOT be named after the handler type
        transport.Exchanges.Any(e => e.Name == handlerName).ShouldBeFalse(
            $"Exchange should not be named '{handlerName}' (handler type). Exchanges should use the message type name.");
    }

    [Fact]
    public void queue_should_be_bound_to_message_type_exchange()
    {
        var queueName = typeof(HandlerTypeNamingHandler).ToMessageTypeName();
        var messageName = typeof(HandlerTypeNamingMessage).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];

        queue.HasBindings.ShouldBeTrue(
            $"Queue '{queueName}' should have bindings");

        // The queue should be bound to the exchange named after the message type
        queue.Bindings().Any(b => b.ExchangeName == messageName).ShouldBeTrue(
            $"Queue '{queueName}' should be bound to exchange '{messageName}', but found bindings to: {string.Join(", ", queue.Bindings().Select(b => b.ExchangeName))}");
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
