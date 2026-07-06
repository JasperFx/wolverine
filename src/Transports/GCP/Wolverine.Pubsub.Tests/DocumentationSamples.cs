using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Pubsub.Tests;

public class DocumentationSamples
{
    public async Task bootstraping()
    {
        #region sample_basic_setup_to_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Let Wolverine create missing topics and subscriptions as necessary
                    .AutoProvision()

                    // Optionally purge all subscriptions on application startup.
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public async Task for_local_development()
    {
        #region sample_connect_to_pubsub_emulator
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Tries to use GCP Pub/Sub emulator, as it defaults
                    // to EmulatorDetection.EmulatorOrProduction. But you can
                    // supply your own, like EmulatorDetection.EmulatorOnly
                    .UseEmulatorDetection();
            }).StartAsync();

        #endregion
    }

    public async Task enable_system_endpoints()
    {
        #region sample_enable_system_endpoints_in_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .EnableSystemEndpoints();
            }).StartAsync();

        #endregion
    }

    public async Task configuring_listeners()
    {
        #region sample_listen_to_pubsub_topic
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id");

                opts.ListenToPubsubTopic("incoming1");

                // Listen to an existing subscription
                opts.ListenToPubsubSubscription("subscription1", x =>
                {
                    // Other configuration...
                });

                opts.ListenToPubsubTopic("incoming2")

                    // You can optimize the throughput by running multiple listeners
                    // in parallel
                    .ListenerCount(5)
                    .ConfigurePubsubSubscription(options =>
                    {
                        // Optionally configure the subscription itself
                        options.DeadLetterPolicy = new DeadLetterPolicy
                        {
                            DeadLetterTopic = "errors",
                            MaxDeliveryAttempts = 5
                        };
                        options.AckDeadlineSeconds = 60;
                        options.RetryPolicy = new RetryPolicy
                        {
                            MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)),
                            MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10))
                        };
                    });
            }).StartAsync();

        #endregion
    }

    public async Task publishing()
    {
        #region sample_subscriber_rules_for_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id");

                opts
                    .PublishMessage<Message1>()
                    .ToPubsubTopic("outbound1");

                opts
                    .PublishMessage<Message2>()
                    .ToPubsubTopic("outbound2")
                    .ConfigurePubsubTopic(options =>
                    {
                        options.MessageRetentionDuration =
                            Duration.FromTimeSpan(TimeSpan.FromMinutes(10));
                    });
            }).StartAsync();

        #endregion
    }

    public async Task conventional_routing()
    {
        #region sample_conventional_routing_for_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .UseConventionalRouting(convention =>
                    {
                        // Optionally override the default queue naming scheme
                        convention.TopicNameForSender(t => t.Namespace)

                            // Optionally override the default queue naming scheme
                            .QueueNameForListener(t => t.Namespace)

                            // Fine tune the conventionally discovered listeners
                            .ConfigureListeners((listener, builder) =>
                            {
                                var messageType = builder.MessageType;
                                var runtime = builder.Runtime; // Access to basically everything

                                // customize the new queue
                                listener.CircuitBreaker(queue => { });

                                // other options...
                            })

                            // Fine tune the conventionally discovered sending endpoints
                            .ConfigureSending((subscriber, builder) =>
                            {
                                // Similarly, use the message type and/or wolverine runtime
                                // to customize the message sending
                            });
                    });
            }).StartAsync();

        #endregion
    }

    public async Task enable_dead_lettering()
    {
        #region sample_enable_wolverine_dead_lettering_for_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Enable dead lettering for all Wolverine endpoints
                    .EnableDeadLettering(
                        // Optionally configure how the GCP Pub/Sub dead letter itself
                        // is created by Wolverine
                        options =>
                        {
                            options.Topic.MessageRetentionDuration =
                                Duration.FromTimeSpan(TimeSpan.FromDays(14));

                            options.Subscription.MessageRetentionDuration =
                                Duration.FromTimeSpan(TimeSpan.FromDays(14));
                        }
                    );
            }).StartAsync();

        #endregion
    }

    public async Task overriding_wolverine_dead_lettering()
    {
        #region sample_configuring_wolverine_dead_lettering_for_pubsub
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .EnableDeadLettering();

                // No dead letter queueing
                opts.ListenToPubsubTopic("incoming")
                    .DisableDeadLettering();

                // Use a different dead letter queue
                opts.ListenToPubsubTopic("important")
                    .ConfigureDeadLettering(
                        "important_errors",

                        // Optionally configure how the dead letter itself
                        // is built by Wolverine
                        e => { e.TelemetryEnabled = true; }
                    );
            }).StartAsync();

        #endregion
    }

    public async Task use_credential_aca_managed_identity()
    {
        #region sample_pubsub_use_credential_aca_managed_identity
        // Azure Container Apps sets IDENTITY_ENDPOINT and IDENTITY_HEADER automatically
        // when managed identity is enabled. These variables expose the same token endpoint
        // that Azure.Identity's DefaultAzureCredential calls internally on ACA.
        var identityEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")
            ?? throw new InvalidOperationException("IDENTITY_ENDPOINT not set — is managed identity enabled on this Container App?");
        var identityHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER")
            ?? throw new InvalidOperationException("IDENTITY_HEADER not set — is managed identity enabled on this Container App?");

        // Build the WIF external account credential JSON once at startup.
        // Google's SDK handles all subsequent token refresh automatically:
        // it re-calls credential_source.url to get a fresh Azure subject token
        // and re-exchanges it with Google STS — no background task needed.
        var externalAccountJson = $$"""
            {
              "type": "external_account",
              "audience": "//iam.googleapis.com/projects/YOUR_PROJECT_NUMBER/locations/global/workloadIdentityPools/YOUR_POOL_ID/providers/YOUR_PROVIDER_ID",
              "subject_token_type": "urn:ietf:params:oauth:token-type:jwt",
              "token_url": "https://sts.googleapis.com/v1/token",
              "service_account_impersonation_url": "https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/YOUR_SERVICE_ACCOUNT@YOUR_PROJECT.iam.gserviceaccount.com:generateAccessToken",
              "credential_source": {
                "url": "{{identityEndpoint}}?resource=api://AzureADTokenExchange&api-version=2019-08-01",
                "headers": { "x-identity-header": "{{identityHeader}}" },
                "format": { "type": "json", "subject_token_field_name": "access_token" }
              }
            }
            """;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .UseCredential(GoogleCredential.FromJson(externalAccountJson));
            }).StartAsync();

        #endregion
    }

    public async Task use_credential_async_factory()
    {
        #region sample_pubsub_use_credential_async
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Use an async factory when the credential must be fetched at startup —
                    // for example, reading a secret from Azure Key Vault before connecting.
                    .UseCredential(async () =>
                    {
                        // Fetch credential configuration from an async source
                        var json = await File.ReadAllTextAsync("/path/to/wif-credential-config.json");
                        return GoogleCredential.FromJson(json);
                    });
            }).StartAsync();

        #endregion
    }

    public async Task use_credential_wif()
    {
        #region sample_pubsub_use_credential_wif
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Provide a GoogleCredential directly. The credential manages its own
                    // token refresh lifecycle — including Workload Identity Federation (WIF)
                    // scenarios where the application runs outside of GCP (e.g. Azure App Service).
                    .UseCredential(
                        GoogleCredential.FromFile("/path/to/wif-credential-config.json")
                    );
            }).StartAsync();

        #endregion
    }

    public async Task configure_publisher_api_client()
    {
        #region sample_pubsub_configure_publisher_api_client
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Full access to PublisherServiceApiClientBuilder for advanced scenarios
                    // such as custom endpoints or channel credentials.
                    .ConfigurePublisherApiClient(builder =>
                    {
                        builder.Endpoint = "custom.pubsub.endpoint:443";
                    });
            }).StartAsync();

        #endregion
    }

    public async Task configure_subscriber_client()
    {
        #region sample_pubsub_configure_subscriber_client
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // Full access to SubscriberClientBuilder for advanced scenarios
                    // such as tuning flow control or concurrency settings.
                    .ConfigureSubscriberClient(builder =>
                    {
                        builder.Settings = new SubscriberClient.Settings
                        {
                            FlowControlSettings = new FlowControlSettings(
                                maxOutstandingElementCount: 500,
                                maxOutstandingByteCount: 50 * 1024 * 1024
                            ),
                        };
                    });
            }).StartAsync();

        #endregion
    }

    public async Task use_credential_rolling()
    {
        #region sample_pubsub_use_credential_rolling
        // A holder that vends a short-lived GoogleCredential and refreshes it as it nears expiry.
        // Short-lived access tokens minted via GoogleCredential.FromAccessToken cannot refresh
        // themselves, so something has to hand out a new one over time.
        var credentials = new RollingCredentialSource();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")

                    // The async factory is invoked again on every listener (re)connect, so each
                    // reconnect picks up the freshest credential without restarting the host.
                    .UseCredential(() => credentials.GetAsync());
            }).StartAsync();

        #endregion
    }

    public async Task customize_mappers()
    {
        #region sample_configuring_custom_envelope_mapper_for_pubsub
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsub("your-project-id")
                    .UseConventionalRouting()
                    .ConfigureListeners(l => l.UseInterop((e, _) => new CustomPubsubMapper(e)))
                    .ConfigureSenders(s => s.UseInterop((e, _) => new CustomPubsubMapper(e)));
            }).StartAsync();

        #endregion
    }
}

#region sample_custom_pubsub_mapper
public class CustomPubsubMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper
{
    public CustomPubsubMapper(PubsubEndpoint endpoint) : base(endpoint)
    {
    }

    public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
    {
        message.Data = ByteString.CopyFrom(outgoing.Data);
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(PubsubMessage incoming, Envelope envelope)
    {
        if (incoming.Attributes is null)
        {
            return;
        }

        foreach (var pair in incoming.Attributes) envelope.Headers[pair.Key] = pair.Value;
    }

    protected override bool tryReadIncomingHeader(PubsubMessage incoming, string key, out string? value)
    {
        if (incoming.Attributes.TryGetValue(key, out var header))
        {
            value = header;

            return true;
        }

        value = null;

        return false;
    }
}

#endregion

#region sample_pubsub_rolling_credential_source
// A thread-safe source of short-lived credentials. The cached credential is reused until it
// nears expiry, then refreshed once (other callers wait on the same refresh). Because Wolverine
// rebuilds the streaming subscriber client on every listener (re)connect — for example after a
// DEADLINE_EXCEEDED restart — GetAsync runs again each time and the listener transparently
// adopts the latest credential.
public class RollingCredentialSource
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _refreshMargin = TimeSpan.FromMinutes(5);
    private GoogleCredential? _current;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async ValueTask<GoogleCredential> GetAsync()
    {
        // Fast path: the cached credential is still comfortably valid
        if (_current is not null && DateTimeOffset.UtcNow < _expiresAt - _refreshMargin)
        {
            return _current;
        }

        await _lock.WaitAsync();
        try
        {
            // Double-check inside the lock in case another connect already refreshed it
            if (_current is null || DateTimeOffset.UtcNow >= _expiresAt - _refreshMargin)
            {
                var (token, lifetime) = await FetchAccessTokenAsync();
                _current = GoogleCredential.FromAccessToken(token);
                _expiresAt = DateTimeOffset.UtcNow + lifetime;
            }
        }
        finally
        {
            _lock.Release();
        }

        return _current;
    }

    // Replace with a real call to your token-vending source — e.g. Azure Key Vault, AWS Secrets
    // Manager, GCP STS, or an internal token broker. Return the access token and how long it lives.
    private Task<(string token, TimeSpan lifetime)> FetchAccessTokenAsync()
        => throw new NotImplementedException();
}

#endregion