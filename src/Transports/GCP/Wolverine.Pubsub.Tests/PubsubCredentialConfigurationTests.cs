using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubCredentialConfigurationTests
{
    [Fact]
    public void use_credential_sets_credential_on_publisher_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigurePublisherApiClient(b => observed = b.GoogleCredential);

        transport.ConfigurePublisherApiBuilder!.Invoke(new PublisherServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public void use_credential_sets_credential_on_subscriber_api_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigureSubscriberApiClient(b => observed = b.GoogleCredential);

        transport.ConfigureSubscriberApiBuilder!.Invoke(new SubscriberServiceApiClientBuilder());
        observed.ShouldBe(credential);
    }

    [Fact]
    public void use_credential_sets_credential_on_subscriber_client_builder()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var credential = GoogleCredential.FromAccessToken("test-token");
        GoogleCredential? observed = null;

        config.UseCredential(credential);
        config.ConfigureSubscriberClient(b => observed = b.GoogleCredential);

        transport.ConfigureSubscriberClientBuilder!.Invoke(new SubscriberClientBuilder());
        observed.ShouldBe(credential);
    }
}