using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4930, reported with these tests by @luisarellano09: an explicit local queue route for a message type was
/// ignored when the same type was also routed with PublishMessagesToNatsSubject. ExplainRoutingFor showed the
/// additive TopicRouting source producing the NATS route, then StubTransport [terminating] ending the chain, so
/// ExplicitRouting (the local queue) was never consulted. The <c>additive: true</c> overload now lets every
/// other routing rule apply as well; the original overload keeps its behavior.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class dynamic_subject_combined_with_local_route : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public dynamic_subject_combined_with_local_route(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();

        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl))
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        _host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DynamicSubjectWithLocal";

                #region sample_publish_to_nats_subject_and_local_queue

                opts.UseNats(natsUrl).AutoProvision();

                // Every DynamicLocalCommand goes to a NATS subject computed from the message...
                opts.PublishMessagesToNatsSubject<DynamicLocalCommand>(m => $"commands.{m.ListenerName}", additive: true);

                // ...and because that routing is additive, to the local "commands" queue at the same time
                opts.PublishMessage<DynamicLocalCommand>().ToLocalQueue("commands");

                #endregion
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public void routes_to_both_the_dynamic_nats_subject_and_the_explicit_local_queue()
    {
        _host.ShouldNotBeNull("this test needs a NATS server at " + NatsTestHelpers.ResolveUrl());

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _output.WriteLine(runtime.ExplainRoutingFor(typeof(DynamicLocalCommand)).ToText());

        var routes = runtime.RoutingFor(typeof(DynamicLocalCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ToArray();

        routes.ShouldContain(x => x.StartsWith("local://commands"));
        routes.ShouldContain(x => x.StartsWith("nats://"));
    }

    [Fact]
    public async Task publishing_executes_the_local_handler()
    {
        _host.ShouldNotBeNull("this test needs a NATS server at " + NatsTestHelpers.ResolveUrl());

        // Nothing in this test listens on the NATS subject, so the session waits on the local handler only
        var session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(15.Seconds())
            .PublishMessageAndWaitAsync(new DynamicLocalCommand("node1"));

        session.Executed.SingleMessage<DynamicLocalCommand>().ListenerName.ShouldBe("node1");

        // ...and the same publish went to the computed NATS subject too
        session.Sent.Envelopes().ShouldContain(x => x.Destination!.Scheme == "nats" && x.TopicName == "commands.node1");
    }
}


/// <summary>
/// GH-4930: the original overload is unchanged -- the dynamic subject is the only route the explicit local queue
/// rule leaves the message, which is why the additive overload exists
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class dynamic_subject_routing_without_additive
{
    private readonly ITestOutputHelper _output;

    public dynamic_subject_routing_without_additive(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task the_original_overload_still_routes_only_to_nats()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        (await NatsTestHelpers.IsNatsAvailable(natsUrl)).ShouldBeTrue("this test needs NATS at " + natsUrl);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DynamicSubjectWithoutAdditive";
                opts.UseNats(natsUrl).AutoProvision();

                opts.PublishMessage<DynamicLocalCommand>().ToLocalQueue("commands");
                opts.PublishMessagesToNatsSubject<DynamicLocalCommand>(m => $"commands.{m.ListenerName}");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var explanation = runtime.ExplainRoutingFor(typeof(DynamicLocalCommand));
        _output.WriteLine(explanation.ToText());

        var routes = runtime.RoutingFor(typeof(DynamicLocalCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ToArray();

        routes.ShouldHaveSingleItem().ShouldStartWith("nats://");
        explanation.ToText().ShouldContain("[additive]");
        explanation.ToText().ShouldNotContain("pass-through");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record DynamicLocalCommand(string ListenerName);

public static class DynamicLocalCommandHandler
{
    public static void Handle(DynamicLocalCommand command)
    {
    }
}
