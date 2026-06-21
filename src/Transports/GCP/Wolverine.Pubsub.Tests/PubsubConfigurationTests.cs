using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
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

    // Integration test — set EmulatorDetection ONLY via callbacks (not on the transport).
    // Successful send/receive proves the callbacks were applied to the real builders.
    [Fact]
    public async Task configure_callbacks_are_applied_to_live_builders()
    {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("wolverine")
                    .ConfigurePublisherApiClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .ConfigureSubscriberApiClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .ConfigureSubscriberClient(b => b.EmulatorDetection = EmulatorDetection.EmulatorOnly)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<TestPubsubMessage>().ToPubsubTopic("config-callbacks-test");
                opts.ListenToPubsubTopic("config-callbacks-test");
            }).StartAsync();

        try
        {
            var session = await host
                .TrackActivity()
                .IncludeExternalTransports()
                .Timeout(1.Minutes())
                .SendMessageAndWaitAsync(new TestPubsubMessage("callback-applied"));

            session.Received.SingleMessage<TestPubsubMessage>().Name.ShouldBe("callback-applied");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task use_credential_does_not_break_emulator_connection()
    {
        var fakeCredential = GoogleCredential.FromAccessToken("test-token");

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting()
                    .UseCredential(fakeCredential)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<TestPubsubMessage>().ToPubsubTopic("credentials-test");
                opts.ListenToPubsubTopic("credentials-test");
            }).StartAsync();

        try
        {
            var session = await host
                .TrackActivity()
                .IncludeExternalTransports()
                .Timeout(1.Minutes())
                .SendMessageAndWaitAsync(new TestPubsubMessage("credential-set"));

            session.Received.SingleMessage<TestPubsubMessage>().Name.ShouldBe("credential-set");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
