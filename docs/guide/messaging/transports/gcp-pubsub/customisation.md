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

The factory is invoked once per client builder during startup. If you need to share a single fetch across all three builders, close over a cached `Task<GoogleCredential>` in your factory lambda.

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
