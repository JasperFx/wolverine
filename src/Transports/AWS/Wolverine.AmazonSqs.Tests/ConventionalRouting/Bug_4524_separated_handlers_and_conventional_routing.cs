using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

/// <summary>
/// GH-4524. Conventional routing under MultipleHandlerBehavior.Separated used to fail at start-up with
/// NotSupportedException on SQS. SQS has no fan-out primitive, so the shape is: the first handler keeps the
/// message type's own queue, every other handler gets a queue named from its handler type, and the SENDER
/// publishes to all of them -- the core now lets a convention name those additional destinations.
/// </summary>
// One host for the whole class, and queues unique to this run rather than AutoPurgeOnStartup(): SQS
// discards messages sent in the minute after a PurgeQueue, so a purge on every start-up made the
// end-to-end test fail whenever the class ran twice within a minute (verified against LocalStack)
public class Bug4524Fixture : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .PrefixIdentifiers($"b4524-{Guid.NewGuid().ToString("N")[..6]}")
                    .AutoProvision()
                    .UseConventionalRouting(x => x.IncludeTypes(t => t == typeof(SeparatedSqsMessage)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // The two sticky handlers below are [WolverineIgnore]d so no other host in this assembly
                // discovers them: two sticky handlers for one message type change what a host provisions
                opts.Discovery.IncludeType<SeparatedSqsConsumerOne>().IncludeType<SeparatedSqsConsumerTwo>();
            }).StartAsync();
    }

    // StopAsync, not just Dispose -- see the note in ConventionalRoutingContext (GH-3763)
    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class Bug_4524_separated_handlers_and_conventional_routing(Bug4524Fixture fixture, ITestOutputHelper output)
    : IClassFixture<Bug4524Fixture>
{
    private IHost _host => fixture.Host;

    private static string messageTypeQueue(AmazonSqsTransport transport)
        => transport.MaybeCorrectName(typeof(SeparatedSqsMessage).ToMessageTypeName());

    [Fact]
    public void each_separated_handler_listens_on_a_queue_named_from_its_handler_type()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<AmazonSqsTransport>();
        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        foreach (var uri in uris) output.WriteLine(uri.ToString());

        uris.ShouldContain(new Uri($"sqs://{AmazonSqsMessageRoutingConvention.QueueNameForSeparatedHandler(transport, typeof(SeparatedSqsConsumerOne))}"));
        uris.ShouldContain(new Uri($"sqs://{AmazonSqsMessageRoutingConvention.QueueNameForSeparatedHandler(transport, typeof(SeparatedSqsConsumerTwo))}"));

        // Both handlers are sticky, so Separated moved both out of the main chain and nothing listens on
        // the message type's own queue
        uris.ShouldNotContain(new Uri($"sqs://{messageTypeQueue(transport)}"));
    }

    [Fact]
    public void the_sender_publishes_to_every_per_handler_queue_and_not_to_the_unlistened_one()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<AmazonSqsTransport>();

        var destinations = runtime.RoutingFor(typeof(SeparatedSqsMessage)).Routes
            .OfType<MessageRoute>().Select(x => x.Uri).ToArray();
        foreach (var uri in destinations) output.WriteLine(uri.ToString());

        destinations.ShouldContain(new Uri($"sqs://{AmazonSqsMessageRoutingConvention.QueueNameForSeparatedHandler(transport, typeof(SeparatedSqsConsumerOne))}"));
        destinations.ShouldContain(new Uri($"sqs://{AmazonSqsMessageRoutingConvention.QueueNameForSeparatedHandler(transport, typeof(SeparatedSqsConsumerTwo))}"));

        // The message type's own queue has no listener in this process, so publishing there would only
        // pile up copies nobody consumes
        destinations.ShouldNotContain(new Uri($"sqs://{messageTypeQueue(transport)}"));
    }

    [Fact]
    public async Task every_separated_handler_receives_the_message()
    {
        var message = new SeparatedSqsMessage(Guid.NewGuid());
        var tracked = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        foreach (var record in tracked.AllRecordsInOrder())
        {
            output.WriteLine(record.ToString());
        }

        // One copy per handler: the message type's queue for the first, the per-handler queue for the second
        tracked.Received.MessagesOf<SeparatedSqsMessage>().Count().ShouldBe(2);
    }
}

public record SeparatedSqsMessage(Guid Id);

[WolverineIgnore]
[StickyHandler(nameof(SeparatedSqsConsumerOne))]
public class SeparatedSqsConsumerOne : IWolverineHandler
{
    public void Consume(SeparatedSqsMessage message)
    {
    }
}

[WolverineIgnore]
[StickyHandler(nameof(SeparatedSqsConsumerTwo))]
public class SeparatedSqsConsumerTwo : IWolverineHandler
{
    public void Consume(SeparatedSqsMessage message)
    {
    }
}
