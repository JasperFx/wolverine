# HTTP Messaging Transport

On top of everything else that Wolverine does, the `WolverineFx.HTTP` Nuget also contains the ability to use HTTP as a
messaging transport for Wolverine messaging. Assuming you have that library attached to your AspNetCore project, add
this declaration to your `WebApplication` in your `Program.Main()` method:

<!-- snippet: sample_MapWolverineHttpTransportEndpoints -->
<a id='snippet-sample_MapWolverineHttpTransportEndpoints'></a>
```cs
app.MapWolverineHttpTransportEndpoints();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L292-L296' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_MapWolverineHttpTransportEndpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The declaration above is actually using Minimal API rather than native Wolverine.HTTP endpoints, but that's perfectly fine 
in this case. That declaration also enables you to use Minimal API's Fluent Interface to customize the authorization
rules against the HTTP endpoints for Wolverine messaging. 

To establish publishing rules in your application to a remote endpoint in another system, use this syntax:

<!-- snippet: sample_publishing_rules_for_http -->
<a id='snippet-sample_publishing_rules_for_http'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PublishAllMessages()
            .ToHttpEndpoint("https://binary.com/api");
    })
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Transport/HttpTransportConfigurationTests.cs#L100-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_rules_for_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
This functionality if very new, and you may want to reach out through Discord for any questions. 
:::
