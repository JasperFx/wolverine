using NSubstitute;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

/// <summary>
/// GH-3492 (RO6). The RabbitMQ client's dispatch concurrency was only settable transport-wide,
/// which made the single most effective Inline scaling lever an all-or-nothing process setting.
/// </summary>
public class consumer_dispatch_concurrency_3492
{
    private static IWolverineRuntime runtime()
    {
        var wolverineRuntime = Substitute.For<IWolverineRuntime>();
        wolverineRuntime.Options.Returns(new WolverineOptions());
        return wolverineRuntime;
    }

    [Fact]
    public void defaults_to_null_so_the_transport_wide_value_still_applies()
    {
        new RabbitMqQueue("foo", new RabbitMqTransport()).ConsumerDispatchConcurrency.ShouldBeNull();
    }

    [Fact]
    public void listener_configuration_sets_it()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        var expression = new RabbitMqListenerConfiguration(endpoint, new RabbitMqTransport());

        expression.ConsumerDispatchConcurrency(20).ShouldBeSameAs(expression);

        endpoint.Compile(runtime());

        endpoint.ConsumerDispatchConcurrency.ShouldBe((ushort)20);
    }

    [Fact]
    public void must_be_at_least_one()
    {
        var endpoint = new RabbitMqQueue("foo", new RabbitMqTransport());
        var expression = new RabbitMqListenerConfiguration(endpoint, new RabbitMqTransport());

        Should.Throw<ArgumentOutOfRangeException>(() => expression.ConsumerDispatchConcurrency(0));
    }
}
