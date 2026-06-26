# Customisation

Wolverine exposes hooks to configure the underlying GCP client builders before they are built. This is useful for cross-cloud authentication (such as Workload Identity Federation), custom service endpoints, or advanced streaming pull settings.

## Providing Credentials

By default, Wolverine uses [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials) (ADC) to authenticate with GCP Pub/Sub. If you need to provide a specific `GoogleCredential` — for example when your application runs outside of GCP and authenticates via Workload Identity Federation — use `UseCredential`:

<!-- snippet: sample_pubsub_use_credential_wif -->
<a id='snippet-sample_pubsub_use_credential_wif'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_use_credential_wif' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`GoogleCredential` manages its own token refresh lifecycle. When built from a Workload Identity Federation credential config file or in-code equivalent, it handles the subject token fetch and GCP STS exchange automatically — no background refresh task is needed in your application.
:::

`UseCredential` is a convenience shorthand that applies the credential to all three underlying GCP client builders (publisher API client, subscriber API client, and subscriber streaming client). For finer-grained control, use the individual `Configure*` methods described below.

### Azure managed identity (Azure Container Apps)

When the application runs in Azure Container Apps with managed identity enabled, ACA automatically sets `IDENTITY_ENDPOINT` and `IDENTITY_HEADER` environment variables that expose the Azure token endpoint. These are the same endpoint that `Azure.Identity`'s `DefaultAzureCredential` calls internally on ACA.

Rather than using `DefaultAzureCredential` and manually bridging to Google's SDK (which has no direct hook for Azure token providers), configure a [Workload Identity Federation](https://cloud.google.com/iam/docs/workload-identity-federation) external account credential that points to those environment variables. Google's SDK will then call the endpoint itself each time a token refresh is needed — no background task or `Azure.Identity` package required.

<!-- snippet: sample_pubsub_use_credential_aca_managed_identity -->
<a id='snippet-sample_pubsub_use_credential_aca_managed_identity'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_use_credential_aca_managed_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The `audience`, pool ID, provider ID, and service account values all come from your GCP Workload Identity Federation setup. The `resource=api://AzureADTokenExchange` query parameter must match the audience configured on your Azure Entra ID application registration that the WIF provider trusts.
:::

### Async credential factory

When the credential itself must be constructed asynchronously at startup — for example, reading a secret from Azure Key Vault, or calling a configuration service before building the `GoogleCredential` — use the async overload:

<!-- snippet: sample_pubsub_use_credential_async -->
<a id='snippet-sample_pubsub_use_credential_async'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_use_credential_async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The factory is **not** a one-time, startup-only hook. It runs again every time Wolverine constructs one of the underlying client builders:

- the publisher and subscriber **API** client builders are rebuilt whenever the transport (re)connects;
- the streaming **subscriber** client is rebuilt on *every listener (re)connect* — including each automatic restart after a transient fault such as `DEADLINE_EXCEEDED`.

This per-connect re-invocation is deliberate, and it is the foundation for [rolling credentials](#rolling-credentials) below.

::: tip
Because the factory can fire frequently — once per listener reconnect, not just once at startup — keep it cheap. If you only need a single fetch shared across all three builders, close over a cached `Task<GoogleCredential>` (or use the caching pattern shown under [Rolling credentials](#rolling-credentials)) so you don't hit your secret store on every reconnect.
:::

## Rolling credentials

A `GoogleCredential` built from Application Default Credentials or a Workload Identity Federation config file refreshes its own *access tokens* internally, so for those you do **not** need to do anything special.

Rolling credentials matter when the credential **material itself** rotates and cannot self-refresh — most commonly when you mint short-lived access tokens yourself via `GoogleCredential.FromAccessToken(...)`, or when the underlying credential config is periodically rotated by an external system. A static `FromAccessToken` credential is frozen at the moment it is created and will eventually expire.

Because Wolverine re-invokes the credential factory on every listener (re)connect, you can hand out a freshly-minted credential over time and have running listeners adopt it automatically — no host restart required. Wrap your token source in a small holder that caches the current credential and refreshes it as it nears expiry:

<!-- snippet: sample_pubsub_use_credential_rolling -->
<a id='snippet-sample_pubsub_use_credential_rolling'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_use_credential_rolling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `RollingCredentialSource` below caches the current credential, refreshes it once when it nears expiry (with other concurrent connects awaiting the same refresh), and reuses it on the fast path so a steady stream of reconnects doesn't hammer your secret store:

<!-- snippet: sample_pubsub_rolling_credential_source -->
<a id='snippet-sample_pubsub_rolling_credential_source'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_rolling_credential_source' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
The credential is captured by the streaming subscriber client *at the point of each connect*. An already-running listener will not swap credentials mid-flight — it adopts the refreshed credential on its **next** (re)connect. Size your `_refreshMargin` so a fresh credential is always handed out comfortably before the old one expires.
:::

## Customising the API Client Builders

For advanced scenarios — custom service endpoints, channel credentials, quota project overrides, or streaming pull settings — Wolverine exposes `Action<TBuilder>` callbacks for each of the three GCP client builders it constructs:

| Method | Builder type | Affects |
|---|---|---|
| `ConfigurePublisherApiClient` | `PublisherServiceApiClientBuilder` | Topic/subscription management (create, delete) and publishing |
| `ConfigureSubscriberApiClient` | `SubscriberServiceApiClientBuilder` | Subscription management (create, delete) |
| `ConfigureSubscriberClient` | `SubscriberClientBuilder` | Streaming message receive (one per listener) |

Each callback fires after `EmulatorDetection` has been applied, so it may override transport-level defaults.

### Custom publisher endpoint

<!-- snippet: sample_pubsub_configure_publisher_api_client -->
<a id='snippet-sample_pubsub_configure_publisher_api_client'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_configure_publisher_api_client' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Custom subscriber flow control settings

<!-- snippet: sample_pubsub_configure_subscriber_client -->
<a id='snippet-sample_pubsub_configure_subscriber_client'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pubsub_configure_subscriber_client' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Multiple calls to any `Configure*` method compose in order — all callbacks are applied, not just the last one.

For emulator setup and basic connection options, see [Connecting to the Broker](/guide/messaging/transports/gcp-pubsub/).
