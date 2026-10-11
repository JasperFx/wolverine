using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4956. Resource setup -- AddResourceSetupOnStartup() and `resources setup` -- used to create a default-shaped
/// work-queue stream for a missing JetStream stream even when the application had DECLARED that stream through
/// DefineStream(). Under CreateOnly the declaration then never corrected it, and Verify reported the drift against
/// a stream Wolverine itself had created. Now a declared stream is created from its declaration; the default shape
/// is only for the undeclared case.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class Gh4956_setup_creates_the_declared_stream
{
    private const long OneMegabyte = 1024 * 1024;

    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public Gh4956_setup_creates_the_declared_stream(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task resource_setup_creates_the_declared_stream_and_it_verifies_clean()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DECLARED_{suffix}";
        var subject = $"declared.{suffix}.orders";

        Action<NatsTransportExpression> declare = nats => nats
            .DefineStream(stream, s => s
                .WithSubjects($"declared.{suffix}.>")
                .WithLimits(maxMessages: 500, maxBytes: 2 * OneMegabyte, maxAge: 1.Hours())
                .WithDeduplicationWindow(5.Minutes()));

        using (var host = await startAsync(declare,
                   opts => opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream)))
        {
            var config = await setUpTheMissingStreamAsync(host, subject, stream);
            _output.WriteLine($"{config.Retention} {config.Discard} msgs={config.MaxMsgs} bytes={config.MaxBytes} subjects={string.Join(",", config.Subjects ?? [])}");

            // The declaration, not the default work-queue shape
            config.Retention.ShouldBe(StreamConfigRetention.Limits);
            config.MaxMsgs.ShouldBe(500);
            config.MaxBytes.ShouldBe(2 * OneMegabyte);
            config.MaxAge.ShouldBe(1.Hours());
            config.DuplicateWindow.ShouldBe(5.Minutes());

            // The declared wildcard already covers the endpoint's subject, so nothing was added to it
            config.Subjects.ShouldBe([$"declared.{suffix}.>"]);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // ...and the stream resource setup created compares clean against the declaration
        using var verified = await startAsync(nats => declare(nats.Provisioning(NatsProvisioning.Verify)), _ => { });
        await verified.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_endpoint_subject_the_declaration_does_not_cover_is_added()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DECLARED_EXTRA_{suffix}";
        var declaredSubject = $"declared-extra.{suffix}.a";
        var endpointSubject = $"declared-extra.{suffix}.b";

        using var host = await startAsync(
            nats => nats.DefineStream(stream, s => s.WithSubjects(declaredSubject)),
            opts => opts.PublishMessage<OrderPlaced>().ToNatsSubject(endpointSubject).UseJetStream(stream));

        var config = await setUpTheMissingStreamAsync(host, endpointSubject, stream);

        config.Subjects.ShouldNotBeNull().OrderBy(x => x).ToArray()
            .ShouldBe(new[] { declaredSubject, endpointSubject }.OrderBy(x => x).ToArray());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_undeclared_stream_still_gets_the_default_work_queue_shape()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"UNDECLARED_{suffix}";
        var subject = $"undeclared.{suffix}.orders";

        using var host = await startAsync(_ => { },
            opts => opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream));

        var config = await setUpTheMissingStreamAsync(host, subject, stream);

        config.Retention.ShouldBe(StreamConfigRetention.Workqueue);
        config.Discard.ShouldBe(StreamConfigDiscard.Old);
        config.Subjects.ShouldBe([subject]);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Start-up provisioning (ConnectAsync) creates every declared stream before any endpoint is set up, so to
    /// reach the endpoint's own missing-stream branch -- the one `resources setup` and a JetStream endpoint whose
    /// stream vanished rely on -- the stream is deleted after the host started and the endpoint's SetupAsync is
    /// run by itself, the way the resource setup runs it
    /// </summary>
    private async Task<StreamConfig> setUpTheMissingStreamAsync(IHost host, string subject, string stream)
    {
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        try
        {
            await js.DeleteStreamAsync(stream, TestContext.Current.CancellationToken);
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            // nothing to delete, which is the undeclared case
        }

        var endpoint = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().EndpointForSubject(subject);
        await endpoint.SetupAsync(NullLogger.Instance);

        return (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
    }

    [Theory]
    [InlineData("orders.>", "orders.eu.created", true)]
    [InlineData("orders.*", "orders.created", true)]
    [InlineData("orders.*", "orders.eu.created", false)]
    [InlineData("orders.created", "orders.created", true)]
    [InlineData("orders.created", "orders.shipped", false)]
    [InlineData("orders.>", "orders", false)]
    [InlineData("*.created", "orders.created", true)]
    public void subject_coverage_follows_nats_wildcard_rules(string pattern, string subject, bool covered)
    {
        NatsEndpoint.SubjectCovers(pattern, subject).ShouldBe(covered);
    }

    private async Task<IHost> startAsync(Action<NatsTransportExpression> configure, Action<WolverineOptions> options)
    {
        return await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Declared";
                var nats = opts.UseNats(_fixture.ConnectionString);
                configure(nats);
                opts.Policies.DisableConventionalLocalRouting();
                options(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }
}
