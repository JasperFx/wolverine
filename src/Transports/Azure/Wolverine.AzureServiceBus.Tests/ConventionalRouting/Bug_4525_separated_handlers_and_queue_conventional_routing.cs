using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

/// <summary>
/// GH-4525. The QUEUE convention under MultipleHandlerBehavior.Separated used to fail at start-up with
/// NotSupportedException; the topic convention has supported it since GH-1684. A queue does not fan out, so
/// the shape is the SQS one: the first handler keeps the message type's own queue, every other handler gets a
/// queue named from its handler type, and the sender publishes to all of them.
/// </summary>
public class Bug4525Fixture : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .PrefixIdentifiers($"b4525-{Guid.NewGuid().ToString("N")[..6]}")
                    .AutoProvision()
                    .UseConventionalRouting(x => x.IncludeTypes(t => t == typeof(SeparatedQueueMessage)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // The two sticky handlers below are [WolverineIgnore]d so no other host in this assembly
                // discovers them: two sticky handlers for one message type change what a host provisions
                opts.Discovery.IncludeType<SeparatedQueueConsumerOne>().IncludeType<SeparatedQueueConsumerTwo>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }
}

public class Bug_4525_separated_handlers_and_queue_conventional_routing(Bug4525Fixture fixture, ITestOutputHelper output)
    : IClassFixture<Bug4525Fixture>
{
    private IHost _host => fixture.Host;

    private static string messageTypeQueue(AzureServiceBusTransport transport)
        => transport.MaybeCorrectName(typeof(SeparatedQueueMessage).ToMessageTypeName());

    private static string perHandler(AzureServiceBusTransport transport, Type handlerType)
        => AzureServiceBusMessageRoutingConvention.QueueNameForSeparatedHandler(transport, handlerType);

    [Fact]
    public void each_separated_handler_listens_on_a_queue_named_from_its_handler_type()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        foreach (var uri in uris) output.WriteLine(uri.ToString());

        uris.ShouldContain(new Uri($"asb://queue/{perHandler(transport, typeof(SeparatedQueueConsumerOne))}"));
        uris.ShouldContain(new Uri($"asb://queue/{perHandler(transport, typeof(SeparatedQueueConsumerTwo))}"));

        // Both handlers are sticky, so Separated moved both out of the main chain and nothing listens on
        // the message type's own queue
        uris.ShouldNotContain(new Uri($"asb://queue/{messageTypeQueue(transport)}"));
    }

    [Fact]
    public void the_sender_publishes_to_every_per_handler_queue_and_not_to_the_unlistened_one()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var destinations = runtime.RoutingFor(typeof(SeparatedQueueMessage)).Routes
            .OfType<MessageRoute>().Select(x => x.Uri).ToArray();
        foreach (var uri in destinations) output.WriteLine(uri.ToString());

        destinations.ShouldContain(new Uri($"asb://queue/{perHandler(transport, typeof(SeparatedQueueConsumerOne))}"));
        destinations.ShouldContain(new Uri($"asb://queue/{perHandler(transport, typeof(SeparatedQueueConsumerTwo))}"));
        destinations.ShouldNotContain(new Uri($"asb://queue/{messageTypeQueue(transport)}"));
    }

    [Fact]
    public async Task every_separated_handler_receives_the_message()
    {
        var message = new SeparatedQueueMessage(Guid.NewGuid());
        var tracked = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        foreach (var record in tracked.AllRecordsInOrder())
        {
            output.WriteLine(record.ToString());
        }

        tracked.Received.MessagesOf<SeparatedQueueMessage>().Count().ShouldBe(2);
    }
}

public record SeparatedQueueMessage(Guid Id);

[WolverineIgnore]
[StickyHandler(nameof(SeparatedQueueConsumerOne))]
public class SeparatedQueueConsumerOne : IWolverineHandler
{
    public void Consume(SeparatedQueueMessage message)
    {
    }
}

[WolverineIgnore]
[StickyHandler(nameof(SeparatedQueueConsumerTwo))]
public class SeparatedQueueConsumerTwo : IWolverineHandler
{
    public void Consume(SeparatedQueueMessage message)
    {
    }
}
