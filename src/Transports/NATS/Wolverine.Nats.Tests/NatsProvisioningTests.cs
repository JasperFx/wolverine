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
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>Provisioning(...)</c> decides what Wolverine does with a declared stream, or a JetStream listener's named
/// consumer, that already exists on the server. <see cref="NatsProvisioning.CreateOnly"/> -- the default --
/// leaves it alone, as Wolverine always has; <see cref="NatsProvisioning.CreateOrUpdate"/> brings the settings
/// Wolverine manages in line with the configuration; <see cref="NatsProvisioning.Verify"/> changes nothing and
/// fails the start with every deviation. None of these tests use resource setup: that path already updates
/// named consumers, and the point is what a normal start does.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsProvisioningTests
{
    private const long OneMegabyte = 1024 * 1024;

    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsProvisioningTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task create_or_update_applies_changed_max_bytes()
    {
        var (stream, subject) = uniqueNames("UPDATE");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        // Provisioned earlier with a smaller limit, plus a setting Wolverine does not manage
        await js.CreateStreamAsync(new StreamConfig(stream, [subject])
        {
            MaxBytes = OneMegabyte,
            Description = "maintained by the operations team"
        }, TestContext.Current.CancellationToken);

        using var host = await startAsync(NatsProvisioning.CreateOrUpdate,
            nats => nats.DefineStream(stream, s => s.WithSubjects(subject).WithLimits(maxBytes: 2 * OneMegabyte)));

        var info = (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config;
        info.MaxBytes.ShouldBe(2 * OneMegabyte);
        info.Description.ShouldBe("maintained by the operations team");
    }

    [Fact]
    public async Task create_only_leaves_an_existing_stream_alone()
    {
        var (stream, subject) = uniqueNames("CREATEONLY");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]) { MaxBytes = OneMegabyte },
            TestContext.Current.CancellationToken);

        // The default
        using var host = await startAsync(null,
            nats => nats.DefineStream(stream, s => s.WithSubjects(subject).WithLimits(maxBytes: 2 * OneMegabyte)));

        (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config.MaxBytes.ShouldBe(OneMegabyte);
    }

    [Fact]
    public async Task verify_fails_on_drift()
    {
        var (stream, subject) = uniqueNames("VERIFY");
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]) { MaxBytes = OneMegabyte },
            TestContext.Current.CancellationToken);

        var missing = $"{stream}_MISSING";

        var exception = await Should.ThrowAsync<Exception>(() => startAsync(NatsProvisioning.Verify, nats => nats
            .DefineStream(stream, s => s.WithSubjects(subject).WithLimits(maxBytes: 2 * OneMegabyte))
            .DefineStream(missing, s => s.WithSubjects(subject + ".missing"))));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"stream '{stream}'");
        message.ShouldContain($"MaxBytes: configured {2 * OneMegabyte}, server {OneMegabyte}");
        message.ShouldContain($"stream '{missing}' does not exist");

        // Verify changes nothing
        (await js.GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.Config.MaxBytes.ShouldBe(OneMegabyte);
        await Should.ThrowAsync<NatsJSApiException>(() => js.GetStreamAsync(missing, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task verify_passes_for_streams_wolverine_created()
    {
        var (stream, subject) = uniqueNames("ROUNDTRIP");

        // Every setting the stream declaration surfaces, defaults and "unlimited" values included, has to
        // compare equal to what the server reports back for the stream Wolverine created from it
        Action<NatsTransportExpression> declare = nats => nats
            .DefineStream(stream, s => s
                .WithSubjects(subject, subject + ".>")
                .WithLimits(maxMessages: 1000, maxAge: 1.Hours())
                .WithDeduplicationWindow(5.Minutes())
                .EnableScheduledDelivery())
            .DefineWorkQueueStream(stream + "_WQ", subject + "-wq");

        using (await startAsync(NatsProvisioning.CreateOnly, declare))
        {
        }

        using var verified = await startAsync(NatsProvisioning.Verify, declare);
    }

    [Fact]
    public async Task named_consumer_max_deliver_is_updated()
    {
        var (stream, subject) = uniqueNames("CONSUMER");
        var consumer = $"consumer-{Guid.NewGuid():N}";
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await createStreamWithConsumerAsync(js, stream, subject, consumer, maxDeliver: 1);

        using var host = await startAsync(NatsProvisioning.CreateOrUpdate, _ => { },
            opts => opts.ListenToNatsSubject(subject).UseJetStream(stream, consumer).ConfigureDeadLetterQueue(7));

        var config = (await js.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info.Config;
        config.MaxDeliver.ShouldBe(7);
        config.Description.ShouldBe("maintained by the operations team");
    }

    [Fact]
    public async Task named_consumer_is_reconciled_by_default()
    {
        var (stream, subject) = uniqueNames("CONSUMERDEFAULT");
        var consumer = $"consumer-{Guid.NewGuid():N}";
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await createStreamWithConsumerAsync(js, stream, subject, consumer, maxDeliver: 1);

        // Changing MaxDeliver in code has to reach the consumer without opting into anything
        using var host = await startAsync(null, _ => { },
            opts => opts.ListenToNatsSubject(subject).UseJetStream(stream, consumer).ConfigureDeadLetterQueue(7));

        var config = (await js.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info.Config;
        config.MaxDeliver.ShouldBe(7);
        config.Description.ShouldBe("maintained by the operations team");
    }

    [Fact]
    public async Task create_only_leaves_a_named_consumer_alone()
    {
        var (stream, subject) = uniqueNames("CONSUMERCREATEONLY");
        var consumer = $"consumer-{Guid.NewGuid():N}";
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await createStreamWithConsumerAsync(js, stream, subject, consumer, maxDeliver: 1);

        // The opt-out for consumers managed outside the application
        using var host = await startAsync(NatsProvisioning.CreateOnly, _ => { },
            opts => opts.ListenToNatsSubject(subject).UseJetStream(stream, consumer).ConfigureDeadLetterQueue(7));

        (await js.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info.Config.MaxDeliver
            .ShouldBe(1);
    }

    [Fact]
    public async Task verify_fails_on_named_consumer_drift()
    {
        var (stream, subject) = uniqueNames("CONSUMERVERIFY");
        var consumer = $"consumer-{Guid.NewGuid():N}";
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();
        await createStreamWithConsumerAsync(js, stream, subject, consumer, maxDeliver: 1);

        var exception = await Should.ThrowAsync<Exception>(() => startAsync(NatsProvisioning.Verify, _ => { },
            opts => opts.ListenToNatsSubject(subject).UseJetStream(stream, consumer).ConfigureDeadLetterQueue(7)));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"consumer '{consumer}'");
        message.ShouldContain("MaxDeliver: configured 7, server 1");

        (await js.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info.Config.MaxDeliver
            .ShouldBe(1);
    }

    [Fact]
    public async Task verify_runs_with_auto_provision_disabled()
    {
        var (stream, subject) = uniqueNames("VERIFYNOAUTO");

        // AutoProvision off is the natural companion of Verify for streams managed outside the application,
        // and Verify creates nothing, so it has to check the declared streams all the same
        var exception = await Should.ThrowAsync<Exception>(() => startAsync(NatsProvisioning.Verify,
            nats => nats.DefineStream(stream, s => s.WithSubjects(subject)),
            opts => opts.Transports.GetOrCreate<NatsTransport>().Configuration.AutoProvision = false));

        var message = flatten(exception);
        _output.WriteLine(message);
        message.ShouldContain($"stream '{stream}' does not exist");

        await using var connection = await connectAsync();
        await Should.ThrowAsync<NatsJSApiException>(() => connection.CreateJetStreamContext()
            .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task resource_setup_leaves_nothing_for_verify_to_reconcile_on_a_named_consumer()
    {
        var (stream, subject) = uniqueNames("SETUPVERIFY");
        var consumer = $"consumer-{Guid.NewGuid():N}";

        // Native scheduled send puts a two-subject filter on the consumer. Resource setup and the listener have
        // to agree on it, or every start rewrites the consumer and a Verify host can never come up after
        // resource setup
        Action<NatsTransportExpression> declare = nats => nats
            .DefineStream(stream, s => s.WithSubjects(subject, subject + ".>").EnableScheduledDelivery());
        Action<WolverineOptions> listen = opts =>
            opts.ListenToNatsSubject(subject).UseJetStream(stream, consumer);

        using (await startAsync(NatsProvisioning.CreateOnly, declare, opts =>
               {
                   listen(opts);
                   opts.Services.AddResourceSetupOnStartup();
               }))
        {
        }

        await using var connection = await connectAsync();
        var config = (await connection.CreateJetStreamContext()
            .GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken)).Info.Config;
        _output.WriteLine($"FilterSubject={config.FilterSubject}, FilterSubjects={string.Join(", ", config.FilterSubjects ?? [])}");

        using var verified = await startAsync(NatsProvisioning.Verify, declare, listen);
    }

    private async Task<IHost> startAsync(NatsProvisioning? provisioning, Action<NatsTransportExpression> configure,
        Action<WolverineOptions>? listeners = null)
    {
        return await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Provisioning";
                var nats = opts.UseNats(_fixture.ConnectionString);
                if (provisioning.HasValue)
                {
                    nats.Provisioning(provisioning.Value);
                }

                // A deviation is not going to go away on a retry of the broker initialization
                if (provisioning == NatsProvisioning.Verify)
                {
                    opts.BrokerInitializationTimeout = TimeSpan.Zero;
                }

                configure(nats);

                opts.Policies.DisableConventionalLocalRouting();
                listeners?.Invoke(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task createStreamWithConsumerAsync(INatsJSContext js, string stream, string subject,
        string consumer, int maxDeliver)
    {
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]), TestContext.Current.CancellationToken);
        await js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig
        {
            Name = consumer,
            DurableName = consumer,
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = 30.Seconds(),
            MaxDeliver = maxDeliver,
            Description = "maintained by the operations team"
        }, TestContext.Current.CancellationToken);
    }

    private static string flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(Environment.NewLine, messages);
    }

    private static (string Stream, string Subject) uniqueNames(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"PROVISION_{prefix}_{suffix}", $"provision.{prefix.ToLowerInvariant()}.{suffix}");
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }
}
