# Configuration Extensions

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExtensionSamples.cs#L8-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sampleextension' title='Start of snippet'>anchor</a></sup>
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
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExtensionSamples.cs#L29-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_including_extension' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L33-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_the_transports_from_web_application_factory' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/HostBuilderExtensions.cs#L287-L297' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disableexternaltransports' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/HostBuilderExtensions.cs#L277-L285' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_extension_method_to_disable_external_transports' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In usage, the `IWolverineExtension` objects added to the IoC container are applied *after* the inner configuration
inside your application's `UseWolverine()` set up.


## Wolverine Plugin Modules

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
[assembly: WolverineModule(typeof(Module1Extension))]
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/Module1/Properties/AssemblyInfo.cs#L29-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_module_to_load_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
