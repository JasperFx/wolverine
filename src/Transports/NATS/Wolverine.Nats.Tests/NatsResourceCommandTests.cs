using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The <c>resources</c> commands run against a host that is built but never started, and every operation of the
/// transport's resource reconnects first. Connecting used to provision the declared streams as well, so
/// <c>resources check</c> and <c>resources statistics</c> created missing streams and, under
/// <see cref="NatsProvisioning.CreateOrUpdate"/>, rewrote existing ones -- discarding messages -- while
/// <c>teardown</c> and <c>clear</c> failed under <see cref="NatsProvisioning.Verify"/> before touching anything.
/// Only startup and <c>resources setup</c> provision declared streams now.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsResourceCommandTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsResourceCommandTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task check_reports_a_missing_declared_stream_without_creating_it()
    {
        var (stream, subject) = uniqueNames("CHECKMISSING");

        // Stream provisioning is on by default, which is exactly what made check create the stream and then find it
        using var host = build(null, nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>")),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer"));

        var exception = await Should.ThrowAsync<Exception>(async () => await (await resourceAsync(host)).Check(
            TestContext.Current.CancellationToken));
        _output.WriteLine(exception.Message);
        exception.Message.ShouldContain($"nats://subject/{subject}.in");

        await shouldNotExistAsync(stream);
    }

    [Fact]
    public async Task check_reports_a_differing_declared_stream_without_changing_it()
    {
        var (stream, subject) = uniqueNames("CHECKDRIFT");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);
        await publishAsync(js, $"{subject}.in", 150);

        using var host = build(NatsProvisioning.CreateOrUpdate,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>").WithLimits(maxMessages: 50)),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer"));

        var exception = await Should.ThrowAsync<Exception>(async () => await (await resourceAsync(host)).Check(
            TestContext.Current.CancellationToken));
        _output.WriteLine(exception.Message);
        exception.Message.ShouldContain($"stream '{stream}'");
        exception.Message.ShouldContain("MaxMsgs: configured 50, server -1");

        var info = (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info;
        info.Config.MaxMsgs.ShouldBe(-1);
        info.State.Messages.ShouldBe(150);
    }

    [Fact]
    public async Task check_passes_for_an_existing_stream_that_create_only_leaves_alone()
    {
        var (stream, subject) = uniqueNames("CHECKCREATEONLY");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);

        // The default for streams: startup would not change it either
        using var host = build(null,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>").WithLimits(maxMessages: 50)),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer"));

        await (await resourceAsync(host)).Check(TestContext.Current.CancellationToken);

        (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config
            .MaxMsgs.ShouldBe(-1);
    }

    [Fact]
    public async Task check_ignores_a_differing_stream_startup_would_leave_alone()
    {
        var (stream, subject) = uniqueNames("CHECKAUTOOFF");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);

        // With AutoProvision off, startup under CreateOrUpdate changes nothing, so there is nothing to report
        using var host = build(NatsProvisioning.CreateOrUpdate,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>").WithLimits(maxMessages: 50)),
            opts =>
            {
                opts.Transports.GetOrCreate<NatsTransport>().Configuration.AutoProvision = false;
                opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer");
            });

        await (await resourceAsync(host)).Check(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task statistics_do_not_create_a_declared_stream()
    {
        var (stream, subject) = uniqueNames("STATISTICS");

        using var host = build(null, nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>")),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer"));

        await (await resourceAsync(host)).DetermineStatus(TestContext.Current.CancellationToken);

        await shouldNotExistAsync(stream);
    }

    [Fact]
    public async Task teardown_and_clear_work_under_verify_with_a_differing_stream()
    {
        var (stream, subject) = uniqueNames("TEARDOWN");
        var consumer = $"consumer-{Guid.NewGuid():N}";
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);
        await js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig
        {
            Name = consumer,
            DurableName = consumer,
            FilterSubject = $"{subject}.in",
            AckPolicy = ConsumerConfigAckPolicy.Explicit
        }, TestContext.Current.CancellationToken);
        await publishAsync(js, $"{subject}.in", 3);

        using var host = build(NatsProvisioning.Verify,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>").WithLimits(maxMessages: 50)),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, consumer));

        var resource = await resourceAsync(host);
        await resource.ClearState(TestContext.Current.CancellationToken);
        await resource.Teardown(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken).AsTask());

        var info = (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info;
        info.Config.MaxMsgs.ShouldBe(-1);
        info.State.Messages.ShouldBe(3);
    }

    [Fact]
    public async Task setup_creates_a_declared_stream_from_its_declaration_with_auto_provision_off()
    {
        var (stream, subject) = uniqueNames("SETUP");

        // Resource setup is the explicit way to provision, so AutoProvision = false does not stop it, and the stream
        // is the declared one rather than a work-queue stream with the JetStream defaults' limits
        using var host = build(null,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>").WithLimits(maxMessages: 50)),
            opts =>
            {
                opts.Transports.GetOrCreate<NatsTransport>().Configuration.AutoProvision = false;
                opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer");
            });

        await (await resourceAsync(host)).Setup(TestContext.Current.CancellationToken);

        await using var connection = await connectAsync();
        var config = (await connection.CreateJetStreamContext()
            .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
        config.Retention.ShouldBe(StreamConfigRetention.Limits);
        config.MaxMsgs.ShouldBe(50);
    }

    [Fact]
    public async Task setup_adds_the_endpoint_subjects_a_missing_declared_stream_does_not_cover()
    {
        var (stream, subject) = uniqueNames("SETUPSUBJECTS");

        // As NatsEndpoint.SetupAsync does for a declared stream it finds missing (GH-4956): resource setup provisions
        // the declared streams first, and a stream it creates there must not lose the endpoints' subjects
        using var host = build(null,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.a")),
            opts => opts.PublishMessage<OrderPlaced>().ToNatsSubject($"{subject}.b").UseJetStream(stream));

        await (await resourceAsync(host)).Setup(TestContext.Current.CancellationToken);

        await using var connection = await connectAsync();
        var config = (await connection.CreateJetStreamContext()
            .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
        config.Subjects.ShouldNotBeNull().OrderBy(x => x).ToArray()
            .ShouldBe(new[] { $"{subject}.a", $"{subject}.b" });
    }

    [Fact]
    public async Task setup_under_verify_fails_on_a_missing_declared_stream_and_creates_nothing()
    {
        var (stream, subject) = uniqueNames("SETUPVERIFY");

        using var host = build(NatsProvisioning.Verify,
            nats => nats.DefineStream(stream, s => s.WithSubjects($"{subject}.>")),
            opts => opts.ListenToNatsSubject($"{subject}.in").UseJetStream(stream, "consumer"));

        var exception = await Should.ThrowAsync<Exception>(async () => await (await resourceAsync(host)).Setup(
            TestContext.Current.CancellationToken));
        _output.WriteLine(flatten(exception));
        flatten(exception).ShouldContain($"stream '{stream}' does not exist");

        await shouldNotExistAsync(stream);
    }

    private IHost build(NatsProvisioning? provisioning, Action<NatsTransportExpression> configure,
        Action<WolverineOptions> listeners)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ResourceCommands";
                var nats = opts.UseNats(_fixture.ConnectionString);
                if (provisioning.HasValue)
                {
                    nats.Provisioning(provisioning.Value);
                }

                configure(nats);
                opts.Policies.DisableConventionalLocalRouting();
                listeners(opts);
            })
            .Build();
    }

    /// <summary>
    /// The transport's resource as the <c>resources</c> commands find it: endpoints compiled, nothing connected
    /// </summary>
    private static async Task<IStatefulResource> resourceAsync(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();
        await transport.InitializeEndpointsAsync(runtime);
        transport.TryBuildStatefulResource(runtime, out var resource).ShouldBeTrue();
        return resource!;
    }

    private async Task shouldNotExistAsync(string stream)
    {
        await using var connection = await connectAsync();
        await Should.ThrowAsync<NatsJSApiException>(() => connection.CreateJetStreamContext()
            .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task publishAsync(INatsJSContext js, string subject, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await js.PublishAsync(subject, new byte[] { 1 }, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static string flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        if (exception is AggregateException aggregate)
        {
            messages.AddRange(aggregate.InnerExceptions.Select(flatten));
        }

        return string.Join(Environment.NewLine, messages);
    }

    private static (string Stream, string Subject) uniqueNames(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"RESOURCES_{prefix}_{suffix}", $"resources.{prefix.ToLowerInvariant()}.{suffix}");
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }
}
