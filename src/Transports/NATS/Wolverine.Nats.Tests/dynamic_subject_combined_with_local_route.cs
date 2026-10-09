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
/// Reproduction: an explicit local queue route for a message type is ignored when the same type is also
/// routed with PublishMessagesToNatsSubject. ExplainRoutingFor shows the additive TopicRouting source
/// producing the NATS route, then StubTransport [terminating] ending the chain, so ExplicitRouting
/// (the local queue) is never consulted.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class dynamic_subject_combined_with_local_route : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private string _root = null!;

    public dynamic_subject_combined_with_local_route(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        _root = $"dynamic.local.{Guid.NewGuid():N}";

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
                opts.UseNats(natsUrl).AutoProvision();

                opts.PublishMessage<DynamicLocalCommand>().ToLocalQueue("commands");
                opts.PublishMessagesToNatsSubject<DynamicLocalCommand>(m => $"{_root}.{m.ListenerName}.command");
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
        if (_host == null)
        {
            return;
        }

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
        if (_host == null)
        {
            return;
        }

        var session = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(15.Seconds())
            .PublishMessageAndWaitAsync(new DynamicLocalCommand("node1"));

        session.Executed.SingleMessage<DynamicLocalCommand>().ListenerName.ShouldBe("node1");
    }
}

public record DynamicLocalCommand(string ListenerName);

public static class DynamicLocalCommandHandler
{
    public static void Handle(DynamicLocalCommand command)
    {
    }
}
