using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// Several unrelated applications sharing one Azure Service Bus namespace used to collide on
// Wolverine's own system queues -- the control queue and the dead letter queue are not scoped by
// service name at all, so the dead letter recovery listener of one application happily drained
// another's dead letters. SystemQueuePrefix() gives each application its own set of those queues
// while leaving the *application* queue names alone, which is what PrefixIdentifiers() would have
// broken for cooperating applications that must keep addressing the same queues.
public class system_queue_prefix
{
    private const string ConnectionString =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y";

    // Drives tryBuildSystemEndpoints without ever touching a broker. Both Azure clients on the
    // transport are Lazy, so nothing here connects to anything.
    private static async Task<AzureServiceBusTransport> initializeAsync(WolverineOptions options)
    {
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);

        await transport.InitializeEndpointsAsync(runtime);

        return transport;
    }

    private static AzureServiceBusQueue[] systemQueues(AzureServiceBusTransport transport)
    {
        return transport.Endpoints().Where(x => x.Role == EndpointRole.System).OfType<AzureServiceBusQueue>()
            .ToArray();
    }

    // The default has to stay byte for byte what it always was -- these names address queues that
    // already exist in production namespaces.
    [Fact]
    public async Task no_prefix_leaves_every_system_queue_name_alone()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString);
        options.ListenToAzureServiceBusQueue("one");

        var transport = await initializeAsync(options);

        var queues = systemQueues(transport);
        queues.ShouldContain(x => x.QueueName.StartsWith("wolverine.response."));
        queues.ShouldContain(x => x.QueueName.StartsWith("wolverine.retries."));

        transport.DefaultDeadLetterQueueName.ShouldBe("wolverine-dead-letter-queue");
        transport.Queues["one"].DeadLetterQueueName.ShouldBe("wolverine-dead-letter-queue");
    }

    [Fact]
    public async Task response_and_retry_queues_are_prefixed()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString).SystemQueuePrefix("my-project");

        var transport = await initializeAsync(options);

        var queues = systemQueues(transport);

        // The response queue keeps the service name's casing -- it always has, and lower casing it
        // now would rename the queue out from under any service with a capital letter in its name.
        queues.ShouldContain(x => x.QueueName.StartsWith("my-project.wolverine.response.MyApp."));

        // The retry queue has always been lower cased and sanitized.
        queues.ShouldContain(x => x.QueueName == "my-project.wolverine.retries.myapp");

        transport.RetryQueue.ShouldNotBeNull();
        transport.RetryQueue!.QueueName.ShouldBe("my-project.wolverine.retries.myapp");
        transport.RetryQueue.Role.ShouldBe(EndpointRole.System);
    }

    [Fact]
    public async Task the_default_dead_letter_queue_is_prefixed()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString).SystemQueuePrefix("my-project");
        options.ListenToAzureServiceBusQueue("one");

        var transport = await initializeAsync(options);

        transport.DefaultDeadLetterQueueName.ShouldBe("my-project.wolverine-dead-letter-queue");
        transport.Queues["one"].DeadLetterQueueName.ShouldBe("my-project.wolverine-dead-letter-queue");

        // endpoints() fills in a real endpoint for every referenced dead letter queue name
        transport.Endpoints().OfType<AzureServiceBusQueue>()
            .ShouldContain(x => x.QueueName == "my-project.wolverine-dead-letter-queue");
    }

    // The dead letter queue name resolves on read, so a queue declared before the prefix was
    // configured still picks the prefixed default up.
    [Fact]
    public void queue_declared_before_the_prefix_call_still_gets_the_prefixed_default()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        var configuration = options.UseAzureServiceBus(ConnectionString);
        options.ListenToAzureServiceBusQueue("one");

        configuration.SystemQueuePrefix("my-project");

        options.Transports.GetOrCreate<AzureServiceBusTransport>().Queues["one"].DeadLetterQueueName
            .ShouldBe("my-project.wolverine-dead-letter-queue");
    }

    // A name you supply yourself is fully qualified. Prefixing it would be Wolverine second guessing
    // an explicit choice.
    [Fact]
    public void explicit_transport_default_dead_letter_queue_name_is_not_prefixed()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString)
            .SystemQueuePrefix("my-project")
            .DefaultDeadLetterQueueName("errors");

        options.ListenToAzureServiceBusQueue("one");

        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        transport.DefaultDeadLetterQueueName.ShouldBe("errors");
        transport.Queues["one"].DeadLetterQueueName.ShouldBe("errors");
    }

    [Fact]
    public async Task per_queue_dead_letter_queue_wins_over_the_transport_default()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString)
            .SystemQueuePrefix("my-project")
            .DefaultDeadLetterQueueName("errors");

        options.ListenToAzureServiceBusQueue("one").ConfigureDeadLetterQueue("special");
        options.ListenToAzureServiceBusQueue("two");

        var transport = await initializeAsync(options);

        transport.Queues["one"].DeadLetterQueueName.ShouldBe("special");
        transport.Queues["two"].DeadLetterQueueName.ShouldBe("errors");
    }

    // Null has to keep meaning "disabled" rather than "nobody said", or DisableDeadLetterQueueing()
    // would silently fall back to the transport default.
    [Fact]
    public async Task disabling_dead_letter_queueing_survives_the_transport_default()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString).SystemQueuePrefix("my-project");
        options.ListenToAzureServiceBusQueue("one").DisableDeadLetterQueueing();

        var transport = await initializeAsync(options);

        var queue = transport.Queues["one"];
        queue.DeadLetterQueueName.ShouldBeNull();
        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void configuring_a_dead_letter_queue_that_is_disabled_throws_a_useful_message()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString);

        var queue = options.Transports.GetOrCreate<AzureServiceBusTransport>().Queues["one"];
        queue.DeadLetterQueueName = null;

        Should.Throw<InvalidOperationException>(() => queue.ConfigureDeadLetterQueue(_ => { }));
    }

    [Fact]
    public void control_queue_is_prefixed_when_the_prefix_is_configured_first()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        options.UseAzureServiceBus(ConnectionString)
            .SystemQueuePrefix("my-project")
            .EnableWolverineControlQueues();

        var control = options.Transports.NodeControlEndpoint.ShouldBeOfType<AzureServiceBusQueue>();
        control.QueueName.ShouldStartWith("my-project.wolverine.control.");
        control.Role.ShouldBe(EndpointRole.System);
    }

    // The control queue has to be built eagerly at configuration time -- the message stores and the
    // node agent read NodeControlEndpoint long before the transports initialize -- so a later
    // SystemQueuePrefix() call has to go back and rebuild it.
    [Fact]
    public void control_queue_is_rebuilt_when_the_prefix_is_configured_afterwards()
    {
        var options = new WolverineOptions { ServiceName = "MyApp" };
        var configuration = options.UseAzureServiceBus(ConnectionString).EnableWolverineControlQueues();

        var original = options.Transports.NodeControlEndpoint.ShouldBeOfType<AzureServiceBusQueue>();
        original.QueueName.ShouldStartWith("wolverine.control.");

        configuration.SystemQueuePrefix("my-project");

        var control = options.Transports.NodeControlEndpoint.ShouldBeOfType<AzureServiceBusQueue>();
        control.QueueName.ShouldBe("my-project." + original.QueueName);
        control.ShouldNotBeSameAs(original);
        control.IsListener.ShouldBeTrue();
        control.EndpointName.ShouldBe("Control");

        // The stale queue must not be left behind, or the transport would provision and listen to a
        // queue nothing points at. Enumerate rather than index -- indexing a LightweightCache
        // creates the entry.
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        transport.Queues.Select(x => x.QueueName).ShouldNotContain(original.QueueName);
        transport.Queues.Select(x => x.QueueName).ShouldContain(control.QueueName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void empty_prefixes_are_rejected(string? prefix)
    {
        var options = new WolverineOptions();
        var configuration = options.UseAzureServiceBus(ConnectionString);

        Should.Throw<ArgumentException>(() => configuration.SystemQueuePrefix(prefix!));
    }

    // Azure Service Bus only accepts letters, numbers, '.', '-', '_' and '/' in an entity name, and
    // rejects anything else with a 400 at provisioning time. Sanitizing here means an illegal prefix
    // can never make it as far as the broker.
    [Theory]
    [InlineData("my-project", "my-project")]
    [InlineData("  My_Project! ", "my_project_")]
    [InlineData("my-project.", "my-project")]
    [InlineData("my-project...", "my-project")]
    [InlineData("my project", "my_project")]
    public void prefixes_are_sanitized(string prefix, string expected)
    {
        var options = new WolverineOptions();
        options.UseAzureServiceBus(ConnectionString).SystemQueuePrefix(prefix);

        options.Transports.GetOrCreate<AzureServiceBusTransport>().SystemQueuePrefix.ShouldBe(expected);
    }

    [Fact]
    public void a_prefix_that_sanitizes_away_to_nothing_is_rejected()
    {
        var options = new WolverineOptions();
        var configuration = options.UseAzureServiceBus(ConnectionString);

        Should.Throw<ArgumentException>(() => configuration.SystemQueuePrefix("..."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void empty_default_dead_letter_queue_names_are_rejected(string? name)
    {
        var options = new WolverineOptions();
        var configuration = options.UseAzureServiceBus(ConnectionString);

        Should.Throw<ArgumentException>(() => configuration.DefaultDeadLetterQueueName(name!));
    }
}
