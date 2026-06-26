using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubConfigurationTests
{
    [Fact]
    public async Task configure_publisher_api_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigurePublisherApiClient(_ => called = true);

        transport.ConfigurePublisherApiBuilder.ShouldNotBeNull();
        await transport.ConfigurePublisherApiBuilder(new PublisherServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task configure_subscriber_api_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberApiClient(_ => called = true);

        transport.ConfigureSubscriberApiBuilder.ShouldNotBeNull();
        await transport.ConfigureSubscriberApiBuilder(new SubscriberServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task configure_subscriber_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberClient(_ => called = true);

        transport.ConfigureSubscriberClientBuilder.ShouldNotBeNull();
        await transport.ConfigureSubscriberClientBuilder(new SubscriberClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task multiple_configure_publisher_api_client_calls_compose_in_order()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var order = new List<int>();

        config.ConfigurePublisherApiClient(_ => order.Add(1));
        config.ConfigurePublisherApiClient(_ => order.Add(2));

        await transport.ConfigurePublisherApiBuilder!(new PublisherServiceApiClientBuilder());
        order.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task multiple_configure_subscriber_client_calls_compose_in_order()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var order = new List<int>();

        config.ConfigureSubscriberClient(_ => order.Add(1));
        config.ConfigureSubscriberClient(_ => order.Add(2));

        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());
        order.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task async_configure_publisher_api_client_callback_is_awaited()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigurePublisherApiClient(async _ =>
        {
            await Task.Yield();
            called = true;
        });

        await transport.ConfigurePublisherApiBuilder!(new PublisherServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task use_credential_sets_credential_on_publisher_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigurePublisherApiClient(b => observed = b.GoogleCredential);

        await transport.ConfigurePublisherApiBuilder!(new PublisherServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task use_credential_sets_credential_on_subscriber_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigureSubscriberApiClient(b => observed = b.GoogleCredential);

        await transport.ConfigureSubscriberApiBuilder!(new SubscriberServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task use_credential_sets_credential_on_subscriber_client_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigureSubscriberClient(b => observed = b.GoogleCredential);

        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task async_use_credential_factory_sets_credential_on_publisher_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        // Simulate an async credential factory (e.g. fetch from Azure Key Vault)
        config.UseCredential(async () =>
        {
            await Task.Yield();
            return credential;
        });
        config.ConfigurePublisherApiClient(b => observed = b.GoogleCredential);

        await transport.ConfigurePublisherApiBuilder!(new PublisherServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task multiple_configure_subscriber_api_client_calls_compose_in_order()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var order = new List<int>();

        config.ConfigureSubscriberApiClient(_ => order.Add(1));
        config.ConfigureSubscriberApiClient(_ => order.Add(2));

        await transport.ConfigureSubscriberApiBuilder!(new SubscriberServiceApiClientBuilder());
        order.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task async_configure_subscriber_api_client_callback_is_awaited()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberApiClient(async _ =>
        {
            await Task.Yield();
            called = true;
        });

        await transport.ConfigureSubscriberApiBuilder!(new SubscriberServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task async_configure_subscriber_client_callback_is_awaited()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberClient(async _ =>
        {
            await Task.Yield();
            called = true;
        });

        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public async Task async_use_credential_factory_sets_credential_on_subscriber_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(async () =>
        {
            await Task.Yield();
            return credential;
        });
        config.ConfigureSubscriberApiClient(b => observed = b.GoogleCredential);

        await transport.ConfigureSubscriberApiBuilder!(new SubscriberServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task async_use_credential_factory_sets_credential_on_subscriber_client_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(async () =>
        {
            await Task.Yield();
            return credential;
        });
        config.ConfigureSubscriberClient(b => observed = b.GoogleCredential);

        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public async Task subscriber_client_credential_factory_is_invoked_on_each_connect()
    {
        // Each listener (re)connect rebuilds the streaming SubscriberClient and re-applies the
        // configure callback, so an async credential factory runs again every time. This is what
        // lets rolling/rotated credentials be picked up without restarting the application.
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var invocations = 0;

        config.UseCredential(async () =>
        {
            await Task.Yield();
            invocations++;
            return GoogleCredential.FromAccessToken($"token-{invocations}");
        });

        // Simulate two listener connects (e.g. an initial start plus a reconnect after DEADLINE_EXCEEDED)
        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());
        await transport.ConfigureSubscriberClientBuilder!(new SubscriberClientBuilder());

        invocations.ShouldBe(2);
    }

    [Fact]
    public async Task subscriber_client_receives_a_fresh_credential_on_each_connect()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var counter = 0;

        config.UseCredential(async () =>
        {
            await Task.Yield();
            return GoogleCredential.FromAccessToken($"token-{++counter}");
        });

        var first = new SubscriberClientBuilder();
        var second = new SubscriberClientBuilder();
        await transport.ConfigureSubscriberClientBuilder!(first);
        await transport.ConfigureSubscriberClientBuilder!(second);

        // A reconnect must not reuse the credential captured on the previous connect
        first.GoogleCredential.ShouldNotBeSameAs(second.GoogleCredential);
    }

    // Requires the emulator running via `docker compose up -d gcp-pubsub`
    [Fact]
    public async Task configure_callbacks_are_applied_to_live_builders()
    {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        // Transport-level EmulatorDetection is set to ProductionOnly — the callbacks are the
        // only thing that enable emulator connectivity. StartAsync makes live API calls via
        // AutoProvision and awaits listener startup, so a misconfigured builder throws here.
        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("wolverine")
                    .UseEmulatorDetection(EmulatorDetection.ProductionOnly)
                    .ConfigurePublisherApiClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .ConfigureSubscriberApiClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .ConfigureSubscriberClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                var topic = $"config-callbacks-test-{Guid.NewGuid():N}";
                opts.PublishMessage<TestPubsubMessage>().ToPubsubTopic(topic);
                opts.ListenToPubsubTopic(topic);
            }).Build();

        await Should.NotThrowAsync(async () =>
        {
            using (host)
            {
                await host.StartAsync();
            }
        });
    }
}
