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

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/2681.
///
/// When conventional routing is configured with <see cref="NamingSource.FromHandlerType"/>
/// and a single handler type handles two or more message types (typical for sagas
/// or any aggregate-style handler), the handler-named queue is supposed to be bound
/// to the exchange of every message type the handler accepts. Pre-fix, only the
/// FIRST message type's exchange got a binding because
/// <c>RabbitMqMessageRoutingConvention.ApplyListenerRoutingDefaults</c> short-circuits
/// on <c>queue.HasBindings</c> — once the first <c>BindExchange</c> call landed, the
/// second message type's pass through the convention saw a queue with bindings
/// already present and skipped the binding entirely.
///
/// The reporter has a candidate fix; this test pins down the expected behaviour
/// independently so the contract is verifiable from outside the convention.
/// </summary>
public class Bug_2681_handler_type_naming_binds_all_exchanges : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime _runtime;

    public Bug_2681_handler_type_naming_binds_all_exchanges()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .UseConventionalRouting(NamingSource.FromHandlerType);

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(MultiMessageHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    [Fact]
    public void handler_queue_is_bound_to_every_handled_message_exchange()
    {
        var queueName = typeof(MultiMessageHandler).ToMessageTypeName();
        var fooExchange = typeof(MultiMessageFoo).ToMessageTypeName();
        var barExchange = typeof(MultiMessageBar).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];

        queue.HasBindings.ShouldBeTrue($"queue '{queueName}' should have bindings");

        var boundExchanges = queue.Bindings().Select(b => b.ExchangeName).ToArray();

        boundExchanges.ShouldContain(fooExchange,
            $"queue '{queueName}' should be bound to '{fooExchange}'; bound to: {string.Join(", ", boundExchanges)}");

        boundExchanges.ShouldContain(barExchange,
            $"queue '{queueName}' should be bound to '{barExchange}'; bound to: {string.Join(", ", boundExchanges)}");
    }

    [Fact]
    public void both_message_exchanges_were_created()
    {
        // Sanity check: the exchanges themselves are registered (so the binding
        // failure is purely on the queue side, not because we never created the
        // second exchange).
        var fooExchange = typeof(MultiMessageFoo).ToMessageTypeName();
        var barExchange = typeof(MultiMessageBar).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();

        transport.Exchanges.Any(e => e.Name == fooExchange).ShouldBeTrue();
        transport.Exchanges.Any(e => e.Name == barExchange).ShouldBeTrue();
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>
/// Companion guard for the GH-2681 fix: the original <c>queue.HasBindings</c>
/// short-circuit existed so a user-configured custom binding (via
/// <see cref="RabbitMqConventionalListenerConfiguration.BindToExchange{TMessage}(ExchangeType, string?, Dictionary{string, object}?)"/>)
/// wouldn't get a default binding stacked on top. The fix keeps that protected
/// case intact by deduping per-exchange — a queue already bound to the
/// message-type exchange via a custom configuration must not be bound a second
/// time.
/// </summary>
public class Bug_2681_custom_bindings_are_not_double_added : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime _runtime;

    public Bug_2681_custom_bindings_are_not_double_added()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision()
                .UseConventionalRouting(NamingSource.FromHandlerType, conventions =>
                {
                    conventions.ConfigureListeners((listener, _) =>
                    {
                        // Pre-register a custom binding to the same exchange the
                        // default convention would otherwise add. Pre-fix and
                        // post-fix this MUST result in exactly one binding to
                        // that exchange — no stacking.
                        var name = typeof(SinglyHandledMessage).ToMessageTypeName();
                        listener.BindToExchange(ExchangeType.Fanout, name);
                    });
                });

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(SinglyHandledMessageHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    [Fact]
    public void user_custom_binding_is_not_double_added_by_the_convention()
    {
        var queueName = typeof(SinglyHandledMessageHandler).ToMessageTypeName();
        var exchangeName = typeof(SinglyHandledMessage).ToMessageTypeName();

        var transport = _runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];

        var bindingsToTarget = queue.Bindings()
            .Where(b => b.ExchangeName == exchangeName)
            .ToArray();

        bindingsToTarget.Length.ShouldBe(1,
            $"queue '{queueName}' should have exactly one binding to '{exchangeName}', got {bindingsToTarget.Length}: " +
            string.Join(", ", queue.Bindings().Select(b => $"{b.ExchangeName}/{b.BindingKey}")));
    }

    public void Dispose() => _host.Dispose();
}

public record SinglyHandledMessage;

public class SinglyHandledMessageHandler
{
    public void Handle(SinglyHandledMessage message)
    {
    }
}

public record MultiMessageFoo;

public record MultiMessageBar;

public class MultiMessageHandler
{
    public void Handle(MultiMessageFoo message)
    {
    }

    public void Handle(MultiMessageBar message)
    {
    }
}
