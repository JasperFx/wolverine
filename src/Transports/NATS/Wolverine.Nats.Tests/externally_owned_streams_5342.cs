using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5342. A JetStream stream another service owns: Wolverine should create this application's consumer on it, but
/// never the stream, and an endpoint marked externally owned as a whole should use its consumer exactly as it finds
/// it and leave it alone in <c>resources teardown</c>.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class externally_owned_streams_5342
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public externally_owned_streams_5342(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task an_externally_owned_stream_is_not_created_but_the_consumer_is()
    {
        var (stream, subject) = uniqueNames("STREAMOWNED");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        // The publishing service owns the stream, and created it its own way
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]) { MaxMsgs = 500 },
            TestContext.Current.CancellationToken);

        using var host = build(opts =>
        {
            #region sample_nats_stream_is_externally_owned

            opts.ListenToNatsSubject($"{subject}.orders")
                .UseJetStream(stream, "shipping")

                // The stream belongs to the service that publishes to it. Wolverine
                // creates the "shipping" consumer on that stream, but never creates
                // the stream itself
                .StreamIsExternallyOwned();

            #endregion
        });

        var resource = await resourceAsync(host);
        await resource.Setup(TestContext.Current.CancellationToken);

        (await js.GetConsumerAsync(stream, "shipping", TestContext.Current.CancellationToken)).ShouldNotBeNull();

        await resource.Teardown(TestContext.Current.CancellationToken);

        // The consumer is this application's, the stream is not
        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetConsumerAsync(stream, "shipping", TestContext.Current.CancellationToken).AsTask());

        var info = (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info;
        info.Config.MaxMsgs.ShouldBe(500);
    }

    [Fact]
    public async Task a_missing_externally_owned_stream_is_named_and_not_created()
    {
        var (stream, subject) = uniqueNames("STREAMMISSING");

        using var host = build(opts => opts.ListenToNatsSubject($"{subject}.orders")
            .UseJetStream(stream, "shipping")
            .StreamIsExternallyOwned());

        var exception = await Should.ThrowAsync<Exception>(async () =>
            await (await resourceAsync(host)).Setup(TestContext.Current.CancellationToken));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"stream '{stream}' does not exist");
        message.ShouldContain("externally owned");

        await using var connection = await connectAsync();
        await Should.ThrowAsync<NatsJSApiException>(() => connection.CreateJetStreamContext()
            .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task an_externally_owned_endpoint_uses_its_consumer_as_it_finds_it()
    {
        var (stream, subject) = uniqueNames("ENDPOINTOWNED");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        // Another service owns the stream and the consumer this application reads through
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);
        await js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig("billing")
        {
            DurableName = "billing",
            FilterSubject = $"{subject}.invoices",
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = 2.Minutes()
        }, TestContext.Current.CancellationToken);

        IHost configure() => build(opts =>
        {
            #region sample_nats_endpoint_is_externally_owned

            // The stream and the "billing" consumer both belong to another service.
            // Wolverine reads through the consumer exactly as it finds it, and never
            // creates, updates or deletes either of them
            opts.ListenToNatsSubject($"{subject}.invoices")
                .UseJetStream(stream, "billing")
                .ExternallyOwned();

            #endregion
        });

        using (var host = configure())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Under the default CreateOrUpdate, Wolverine would have brought AckWait in line with its own 30 seconds
        var consumer = await js.GetConsumerAsync(stream, "billing", TestContext.Current.CancellationToken);
        consumer.Info.Config.AckWait.ShouldBe(2.Minutes());

        using (var host = configure())
        {
            var resource = await resourceAsync(host);

            // check still covers the endpoint, and its stream exists
            await resource.Check(TestContext.Current.CancellationToken);
            await resource.Teardown(TestContext.Current.CancellationToken);
        }

        (await js.GetConsumerAsync(stream, "billing", TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_missing_consumer_on_an_externally_owned_endpoint_is_named_and_not_created()
    {
        var (stream, subject) = uniqueNames("CONSUMERMISSING");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"{subject}.>"]), TestContext.Current.CancellationToken);

        using var host = build(opts =>
        {
            // Give up after the first attempt rather than retrying for two minutes
            opts.BrokerInitializationTimeout = 1.Seconds();

            opts.ListenToNatsSubject($"{subject}.invoices")
                .UseJetStream(stream, "billing")
                .ExternallyOwned();
        });

        var exception = await Should.ThrowAsync<Exception>(async () =>
            await host.StartAsync(TestContext.Current.CancellationToken));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"consumer 'billing' does not exist on stream '{stream}'");

        await Should.ThrowAsync<NatsJSApiException>(() =>
            js.GetConsumerAsync(stream, "billing", TestContext.Current.CancellationToken).AsTask());
    }

    private IHost build(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ExternallyOwned";
                opts.UseNats(_fixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                configure(opts);
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
        return ($"OWNED_{prefix}_{suffix}", $"owned.{prefix.ToLowerInvariant()}.{suffix}");
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }
}
