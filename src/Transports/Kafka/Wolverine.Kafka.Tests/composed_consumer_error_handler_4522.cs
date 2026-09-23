using Confluent.Kafka;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-4522. Confluent's ConsumerBuilder throws if an error handler is registered twice, so a user handler
/// registered through ConfigureConsumerBuilders used to win outright and Wolverine's connection-state
/// tracking was silently switched off -- permanently, for the whole host. WolverineConsumerBuilder composes
/// the two instead.
/// </summary>
public class composed_consumer_error_handler_4522
{
    private static readonly ConsumerConfig Config = new() { BootstrapServers = "localhost:9092", GroupId = "g" };

    [Fact]
    public void with_no_user_handler_wolverine_registers_its_own()
    {
        var builder = new WolverineConsumerBuilder(Config);

        var wolverineHits = 0;
        builder.ComposeErrorHandler((_, _) => wolverineHits++);

        builder.ComposedWithUserHandler.ShouldBeFalse();

        builder.InvokeErrorHandlerForTesting(new Error(ErrorCode.Local_AllBrokersDown));
        wolverineHits.ShouldBe(1);
    }

    [Fact]
    public void with_a_user_handler_both_run_and_the_user_goes_first()
    {
        var builder = new WolverineConsumerBuilder(Config);

        var order = new List<string>();
        builder.SetErrorHandler((_, _) => order.Add("user"));
        builder.ComposeErrorHandler((_, _) => order.Add("wolverine"));

        builder.ComposedWithUserHandler.ShouldBeTrue();

        builder.InvokeErrorHandlerForTesting(new Error(ErrorCode.Local_AllBrokersDown));

        order.ShouldBe(["user", "wolverine"]);
    }

    [Fact]
    public void a_throwing_user_handler_does_not_cost_wolverine_its_tracking()
    {
        // Wolverine's tracking only ever moves the state toward trouble, so it must not be skippable by a
        // user handler that throws -- otherwise one bad callback silently reinstates the original bug.
        var builder = new WolverineConsumerBuilder(Config);

        var wolverineHits = 0;
        builder.SetErrorHandler((_, _) => throw new DivideByZeroException("boom"));
        builder.ComposeErrorHandler((_, _) => wolverineHits++);

        Should.Throw<DivideByZeroException>(() =>
            builder.InvokeErrorHandlerForTesting(new Error(ErrorCode.Local_AllBrokersDown)));

        wolverineHits.ShouldBe(1);
    }

    [Fact]
    public void composition_feeds_the_connection_state_tracker()
    {
        var builder = new WolverineConsumerBuilder(Config);
        var tracker = new KafkaConnectionStateTracker();

        builder.SetErrorHandler((_, _) => { });
        builder.ComposeErrorHandler((_, error) => tracker.ApplyError(error));

        builder.InvokeErrorHandlerForTesting(new Error(ErrorCode.Local_AllBrokersDown));

        // Before GH-4522 this stayed at Unknown for the lifetime of the host.
        tracker.ConnectionState.ShouldBe(Wolverine.Transports.TransportConnectionState.Disconnected);
    }
}
