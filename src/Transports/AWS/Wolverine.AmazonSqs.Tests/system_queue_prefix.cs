using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-4282, the Amazon SQS counterpart of #4263. The node control queue is composed as
/// <c>"wolverine.control." + node</c> with no service name in it, so two unrelated Wolverine applications
/// sharing one AWS account and region — both running as node 1 — both claim the same queue. The default dead
/// letter queue has the same problem, and there it is damaging rather than untidy: <c>SqsDeadLetterQueueListener</c>
/// drains broker-side dead letters into the Wolverine message store, so whichever application wins the race
/// records another service's failure in <em>its</em> store.
///
/// <para>The response queue was already safe — SQS puts the service name in it — and stays that way.</para>
/// </summary>
public class system_queue_prefix
{
    private static AmazonSqsTransport transportFor(Action<AmazonSqsTransportConfiguration> configure)
    {
        var options = new WolverineOptions { ServiceName = "Orders" };
        var configuration = options.UseAmazonSqsTransportLocally();
        configure(configuration);

        return options.Transports.GetOrCreate<AmazonSqsTransport>();
    }

    [Fact]
    public void without_a_prefix_every_name_is_unchanged()
    {
        // The whole safety property: an application that does not opt in must see byte-for-byte the names
        // it has always seen.
        var transport = transportFor(_ => { });

        transport.PrefixSystemQueueName("wolverine.control.1").ShouldBe("wolverine.control.1");
        transport.DefaultDeadLetterQueueName.ShouldBe(AmazonSqsTransport.DeadLetterQueueName);
    }

    [Fact]
    public void the_prefix_joins_with_the_sqs_identifier_delimiter()
    {
        // '-', not '.', because that is what SQS names allow and what IdentifierDelimiter already says.
        var transport = transportFor(x => x.SystemQueuePrefix("my-project"));

        transport.PrefixSystemQueueName("wolverine.control.1").ShouldBe("my-project-wolverine.control.1");
    }

    [Fact]
    public void the_default_dead_letter_queue_picks_up_the_prefix()
    {
        var transport = transportFor(x => x.SystemQueuePrefix("my-project"));

        transport.DefaultDeadLetterQueueName
            .ShouldBe($"my-project-{AmazonSqsTransport.DeadLetterQueueName}");
    }

    [Fact]
    public void an_explicitly_named_default_dead_letter_queue_is_never_prefixed()
    {
        // A name you typed is a name you meant.
        var transport = transportFor(x => x
            .SystemQueuePrefix("my-project")
            .DefaultDeadLetterQueueName("orders-errors"));

        transport.DefaultDeadLetterQueueName.ShouldBe("orders-errors");
    }

    [Fact]
    public void the_prefix_is_applied_whichever_order_the_two_calls_are_made_in()
    {
        // DefaultDeadLetterQueueName resolves on read, so bootstrap call order cannot matter.
        var before = transportFor(x => x.SystemQueuePrefix("my-project").DefaultDeadLetterQueueName("errors"));
        var after = transportFor(x => x.DefaultDeadLetterQueueName("errors").SystemQueuePrefix("my-project"));

        before.DefaultDeadLetterQueueName.ShouldBe(after.DefaultDeadLetterQueueName);
    }

    [Fact]
    public void a_prefix_set_after_the_control_queue_rebuilds_it()
    {
        // The control queue has to be built eagerly, because NodeControlEndpoint is read by the message
        // stores and the node agent long before transports initialize. Calling SystemQueuePrefix()
        // afterwards therefore has to rebuild it, and take the stale one back out so nothing provisions
        // and listens to a queue nothing points at.
        var options = new WolverineOptions { ServiceName = "Orders" };
        var configuration = options.UseAmazonSqsTransportLocally();
        configuration.EnableWolverineControlQueues();

        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        var unprefixed = transport.ControlQueue!.QueueName;

        configuration.SystemQueuePrefix("my-project");

        var prefixed = transport.ControlQueue!.QueueName;
        prefixed.ShouldNotBe(unprefixed);
        prefixed.ShouldStartWith("my-project-");

        // The stale entry is gone, and the node control endpoint points at the new one.
        transport.Queues.Any(x => x.QueueName == unprefixed).ShouldBeFalse();
        options.Transports.NodeControlEndpoint!.Uri.ShouldBe(transport.ControlQueue.Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void an_empty_prefix_is_refused(string prefix)
    {
        Should.Throw<ArgumentException>(() => transportFor(x => x.SystemQueuePrefix(prefix)));
    }

    [Fact]
    public void a_prefix_with_no_usable_characters_is_refused()
    {
        // Better than silently producing a queue named "_-wolverine...".
        Should.Throw<ArgumentException>(() => transportFor(x => x.SystemQueuePrefix("...")));
    }
}
