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

// GH-3041: with NamingSource.FromHandlerType + MultipleHandlerBehavior.Separated, a message handled by
// BOTH a saga (e.g. a Start method) and a regular handler only created a listener queue for the saga -
// the regular handler's separated chain lives in chain.ByEndpoint, which the FromHandlerType listener
// discovery ignored. Result: the regular handler never received the message.
public class Bug_3041_saga_and_handler_with_handler_type_naming : IAsyncLifetime, IDisposable
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

            opts.Policies.DisableConventionalLocalRouting();
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(Bug3041Saga))
                .IncludeType(typeof(Bug3041RegularHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void both_the_saga_and_the_regular_handler_get_listener_queues()
    {
        var transport = _runtime.Options.RabbitMqTransport();
        var sagaQueue = typeof(Bug3041Saga).ToMessageTypeName();
        var handlerQueue = typeof(Bug3041RegularHandler).ToMessageTypeName();
        var all = string.Join(", ", transport.Queues.Select(q => q.EndpointName));

        transport.Queues.Contains(sagaQueue).ShouldBeTrue($"saga queue '{sagaQueue}' missing. Queues: {all}");
        transport.Queues.Contains(handlerQueue).ShouldBeTrue($"regular-handler queue '{handlerQueue}' missing. Queues: {all}");
    }

    [Fact]
    public void both_handler_queues_have_active_listeners()
    {
        var saga = $"rabbitmq://queue/{typeof(Bug3041Saga).ToMessageTypeName()}".ToUri();
        var handler = $"rabbitmq://queue/{typeof(Bug3041RegularHandler).ToMessageTypeName()}".ToUri();

        var listeners = _runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        listeners.ShouldContain(saga);
        listeners.ShouldContain(handler);
    }

    public void Dispose() => _host.Dispose();
}

public record Bug3041Message;

public class Bug3041Saga : Saga
{
    public Guid Id { get; set; }
    public void Start(Bug3041Message message) => Id = Guid.NewGuid();
}

public class Bug3041RegularHandler
{
    public void Handle(Bug3041Message message)
    {
    }
}
