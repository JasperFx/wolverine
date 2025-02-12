# Configuration Extensions

::: warning
As of Wolverine 3.0 and our move to directly support non-Lamar IoC containers, it is no longer
possible to alter service registrations through Wolverine extensions that are themselves registered
in the IoC container at bootstrapping time.
:::

Wolverine supports the concept of extensions for modularizing Wolverine configuration with implementations of the `IWolverineExtension` interface:

<!-- snippet: sample_IWolverineExtension -->
<a id='snippet-sample_iwolverineextension'></a>
```cs
/// <summary>
///     Use to create loadable extensions to Wolverine applications
/// </summary>
public interface IWolverineExtension
{
    /// <summary>
    ///     Make any alterations to the WolverineOptions for the application
    /// </summary>
    /// <param name="options"></param>
    void Configure(WolverineOptions options);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/IWolverineExtension.cs#L3-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iwolverineextension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's a sample:

<!-- snippet: sample_SampleExtension -->
<a id='snippet-sample_sampleextension'></a>
```cs
public class SampleExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        // Add service registrations
        options.Services.AddTransient<IFoo, Foo>();

        // Alter settings within the application
        options
            .UseNewtonsoftForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.None);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExtensionSamples.cs#L9-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sampleextension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Extensions can be applied programmatically against the `WolverineOptions` like this:

<!-- snippet: sample_including_extension -->
<a id='snippet-sample_including_extension'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Including a single extension
        opts.Include<SampleExtension>();

        // Or add a Wolverine extension that needs
        // to use IoC services
        opts.Services.AddWolverineExtension<ConfigurationUsingExtension>();

    })

    .ConfigureServices(services =>
    {
        // This is the same logical usage, just showing that it
        // can be done directly against IServiceCollection
        services.AddWolverineExtension<ConfigurationUsingExtension>();
    })

    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExtensionSamples.cs#L52-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_including_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lastly, you can also add `IWolverineExtension` types to your IoC container registration that will be applied to `WolverineOptions` just
before bootstrapping Wolverine at runtime. This was originally added to allow for test automation scenarios where you might want
to override part of the Wolverine setup during tests. As an example, consider this common usage for disabling external transports
during testing:

<!-- snippet: sample_disabling_the_transports_from_web_application_factory -->
<a id='snippet-sample_disabling_the_transports_from_web_application_factory'></a>
```cs
// This is using Alba to bootstrap a Wolverine application
// for integration tests, but it's using WebApplicationFactory
// to do the actual bootstrapping
await using var host = await AlbaHost.For<Program>(x =>
{
    // I'm overriding
    x.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L29-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_the_transports_from_web_application_factory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Behind the scenes, Wolverine has a small extension like this:

<!-- snippet: sample_DisableExternalTransports -->
<a id='snippet-sample_disableexternaltransports'></a>
```cs
internal class DisableExternalTransports : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.ExternalTransportsAreStubbed = true;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/HostBuilderExtensions.cs#L441-L451' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disableexternaltransports' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And that extension is just added to the application's IoC container at test bootstrapping time like this:

<!-- snippet: sample_extension_method_to_disable_external_transports -->
<a id='snippet-sample_extension_method_to_disable_external_transports'></a>
```cs
public static IServiceCollection DisableAllExternalWolverineTransports(this IServiceCollection services)
{
    services.AddSingleton<IWolverineExtension, DisableExternalTransports>();
    return services;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/HostBuilderExtensions.cs#L417-L425' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_extension_method_to_disable_external_transports' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In usage, the `IWolverineExtension` objects added to the IoC container are applied *after* the inner configuration
inside your application's `UseWolverine()` set up.

As another example, `IWolverineExtension` objects added to the IoC container can also use services injected into the 
extension object from the IoC container as shown in this example that uses the .NET `IConfiguration` service:

<!-- snippet: sample_configuration_using_extension -->
<a id='snippet-sample_configuration_using_extension'></a>
```cs
public class ConfigurationUsingExtension : IWolverineExtension
{
    private readonly IConfiguration _configuration;

    // Use constructor injection from your DI container at runtime
    public ConfigurationUsingExtension(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void Configure(WolverineOptions options)
    {
        // Configure the wolverine application using
        // the information from IConfiguration
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExtensionSamples.cs#L26-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuration_using_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's also a small helper method to register Wolverine extensions like so:

## Modifying Transport Configuration

If your Wolverine extension needs to apply some kind of extra configuration to the transport integration, most of the 
transport packages support a `WolverineOptions.ConfigureTransportName()` extension method that will let you make
additive configuration changes to the transport integration for items like declaring extra queues, topics, exchanges, subscriptions or overriding
dead letter queue behavior. For example:

1. `ConfigureRabbitMq()`
2. `ConfigureKafka()`
3. `ConfigureAzureServiceBus()`
4. `ConfigureAmazonSqs()`

## Asynchronous Extensions

::: tip
This was added to Wolverine 2.3, specifically for a user needing to use the [Feature Flag library](https://learn.microsoft.com/en-us/azure/azure-app-configuration/use-feature-flags-dotnet-core) from Microsoft. 
:::

There is also any option for creating Wolverine extensions that need to use asynchronous methods to configure
the `WolverineOptions` using the `IAsyncWolverineExtension` library. A sample is shown below:

<!-- snippet: sample_async_Wolverine_extension -->
<a id='snippet-sample_async_wolverine_extension'></a>
```cs
public class SampleAsyncExtension : IAsyncWolverineExtension
{
    private readonly IFeatureManager _features;

    public SampleAsyncExtension(IFeatureManager features)
    {
        _features = features;
    }

    public async ValueTask Configure(WolverineOptions options)
    {
        if (await _features.IsEnabledAsync("Module1"))
        {
            // Make any kind of Wolverine configuration
            options
                .PublishMessage<Module1Message>()
                .ToLocalQueue("module1-high-priority")
                .Sequential();
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/using_async_extensions.cs#L65-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_async_wolverine_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which can be added to your application with this extension method on `IServiceCollection`:

<!-- snippet: sample_registering_async_extension -->
<a id='snippet-sample_registering_async_extension'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddFeatureManagement();
        opts.Services.AddSingleton(featureManager);

        // Adding the async extension to the underlying IoC container
        opts.Services.AddAsyncWolverineExtension<SampleAsyncExtension>();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/using_async_extensions.cs#L43-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_async_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Asynchronous Extensions and Wolverine.HTTP

Just a heads up, there's a timing issue between the application of asynchronous Wolverine extensions
and the usage of the Wolverine.HTTP `MapWolverineEndpoints()` method. If you need the asynchronous 
extensions to apply to the HTTP configuration, you need to help Wolverine out by explicitly calling
this method in your `Program` file *after* building the `WebApplication`, but before calling 
`MapWolverineEndpoints()` like so:

<!-- snippet: sample_calling_ApplyAsyncWolverineExtensions -->
<a id='snippet-sample_calling_applyasyncwolverineextensions'></a>
```cs
var app = builder.Build();

// In order for async Wolverine extensions to apply to Wolverine.HTTP configuration,
// you will need to explicitly call this *before* MapWolverineEndpoints()
await app.Services.ApplyAsyncWolverineExtensions();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L114-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_calling_applyasyncwolverineextensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Wolverine Plugin Modules

::: warning
This functionality will likely be eliminated in Wolverine 3.0. 
:::

::: tip
Use this sparingly, but it might be advantageous for adding extra instrumentation or extra middleware
:::

If you want to create a Wolverine extension assembly that automatically loads itself into an application just
by being referenced by the project, you can use a combination of `IWolverineExtension` and the `[WolverineModule]`
assembly attribute.

Assuming that you have an implementation of `IWolverineExtension` named `Module1Extension`, you can mark your module library
with this attribute to automatically add that extension to Wolverine:

<!-- snippet: sample_using_wolverine_module_to_load_extension -->
<a id='snippet-sample_using_wolverine_module_to_load_extension'></a>
```cs
[assembly: WolverineModule<Module1Extension>]
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/Module1/Properties/AssemblyInfo.cs#L29-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_module_to_load_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disabling Assembly Scanning

Some Wolverine users have seen rare issues with the assembly scanning cratering an application with out of memory
exceptions in the case of an application directory being the same as the root of a Docker container. *If* you experience
that issue, or just want a faster start up time, you can disable the automatic extension discovery using this syntax:

<!-- snippet: sample_disabling_assembly_scanning -->
<a id='snippet-sample_disabling_assembly_scanning'></a>
```cs
using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.DisableConventionalDiscovery();
    }, ExtensionDiscovery.ManualOnly)
    
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/bootstrapping_specs.cs#L66-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_assembly_scanning' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
