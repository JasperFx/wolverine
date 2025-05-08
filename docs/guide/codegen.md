# Working with Code Generation

::: warning
If you are experiencing noticeable startup lags or seeing spikes in memory utilization with an application using
Wolverine, you will want to pursue using either the `Auto` or `Static` modes for code generation as explained in this guide.
:::

::: tip
This blog post from Oskar Dudycz will apply to Wolverine as well: [How to create a Docker image for the Marten application](https://event-driven.io/en/marten_and_docker/)
:::

Wolverine uses runtime code generation to create the "adaptor" code that Wolverine uses to call into 
your message handlers. Wolverine's [middleware strategy](/guide/handlers/middleware) also uses this strategy to "weave" calls to 
middleware directly into the runtime pipeline without requiring the copious usage of adapter interfaces
that is prevalent in most other .NET frameworks.

That's great when everything is working as it should, but there's a couple issues:

1. The usage of the Roslyn compiler at runtime *can sometimes be slow* on its first usage. This can lead to sluggish *cold start*
   times in your application that might be problematic in serverless scenarios for examples.
2. There's a little bit of conventional magic in how Wolverine finds and applies middleware or passed arguments
   to your message handlers or HTTP endpoint handlers.

Not to worry though, Wolverine has several facilities to either preview the generated code for diagnostic purposes to 
really understand how Wolverine is interacting with your code and to optimize the "cold start" by generating the dynamic
code ahead of time so that it can be embedded directly into your application's main assembly and discovered from there.

By default, Wolverine runs with "dynamic" code generation where all the necessary generated types are built on demand
the first time they are needed. This is perfect for a quick start to Wolverine, and might be fine in smaller projects even
at production time.

::: warning
Note that you may need to delete the existing source code when you change
handler signatures or add or remove middleware. Nothing in Wolverine is able
to detect that the generated source code needs to be rewritten
:::

Lastly, you have a couple options about how Wolverine handles the dynamic code generation as shown below:

<!-- snippet: sample_codegen_type_load_mode -->
<a id='snippet-sample_codegen_type_load_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default behavior. Dynamically generate the
        // types on the first usage
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

        // Never generate types at runtime, but instead try to locate
        // the generated types from the main application assembly
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

        // Hybrid approach that first tries to locate the types
        // from the application assembly, but falls back to
        // generating the code and dynamic type. Also writes the
        // generated source code file to disk
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L13-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_codegen_type_load_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At development time, use the `Dynamic` mode if you are actively changing handler
signatures or the application of middleware that might be changing the generated code. 

Even at development time, if the handler signatures are relatively stable, you can use
the `Auto` mode to use pre-generated types locally. This may help you have a quicker
development cycle -- especially if you like to lean heavily on integration testing where
you're quickly starting and stopping your application. The `Auto` mode will write the generated
source code for missing types to the `Internal/Generated` folder under your main application 
project.

At production time, if there is any issue whatsoever with resource utilization, the Wolverine team
recommends using the `Static` mode where all types are assumed to be pre-generated into what Wolverine
thinks is the application assembly (more on this in the troubleshooting guide below).

::: tip
Most of the facilities shown here will require the [Oakton command line integration](./command-line).
:::

## Embedding Codegen in Docker

At this point, the most successful mechanism and sweet spot is to run the codegen as `Dynamic` at development time, but generating
the code artifacts just in time for production deployments. From Wolverine's sibling project Marten, see this section on [Application project setup](https://martendb.io/devops/devops.html#application-project-set-up)
for embedding the code generation directly into your Docker images for deployment.

## Troubleshooting Code Generation Issues

::: warning
There's nothing magic about the `Auto` mode, and Wolverine isn't (yet) doing any file comparisons against the generated code and
the current version of the application. At this point, the Wolverine community recommends against using the `Auto` mode
for code generation as it has not added much value and can cause some confusion.
:::

In all cases, don't hesitate to reach out to the Wolverine team in the Discord link at the top right of this page to 
ask for help with any codegen related issues.

If Wolverine is throwing exceptions in `Static` mode saying that it cannot find the expected pre-generated types, here's 
your checklist of things to check:

Are the expected generated types written to files in the main application project before that project is compiled? The pre-generation
works by having the source code written into the assembly in the first place.

Is Wolverine really using the correct application assembly when it looks for pre-built handlers or HTTP endpoints? Wolverine will log
what *it* thinks is the application assembly upfront, but it can be fooled in certain project structures. To override the application
assembly choice to help Wolverine out, use this syntax:

<!-- snippet: sample_overriding_application_assembly -->
<a id='snippet-sample_overriding_application_assembly'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Override the application assembly to help
        // Wolverine find its handlers
        // Should not be necessary in most cases
        opts.ApplicationAssembly = typeof(Program).Assembly;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/BootstrappingSamples.cs#L10-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_application_assembly' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If the assembly choice is correct, and the expected code files are really in `Internal/Generated` exactly as you'd expect, make
sure there's no accidental `<Exclude />` nodes in your project file. *Don't laugh, that's actually happened to Wolverine users*


## Environment Check for Expected Types

As a new option in Wolverine 1.7.0, you can also add an environment check for the existence of the expected pre-built types
to [fail fast](https://en.wikipedia.org/wiki/Fail-fast) on application startup like this:

<!-- snippet: sample_asserting_all_pre_built_types_exist_upfront -->
<a id='snippet-sample_asserting_all_pre_built_types_exist_upfront'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
    {
        if (builder.Environment.IsProduction())
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

            opts.Services.AddJasperFx(j =>
            {
                // I'm only going to care about this in production
                j.Production.AssertAllPreGeneratedTypesExist = true;
            });
        }
    });

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L38-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asserting_all_pre_built_types_exist_upfront' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that you would have to opt into using the environment checks on application startup, and maybe even force .NET
to make hosted service failures stop the application. 

See [Oakton's Environment Check functionality](https://jasperfx.github.io/oakton/guide/host/environment.html) for more information. 

## Previewing the Generated Code

::: tip
All of these commands are from the JasperFx.CodeGeneration.Commands library that Wolverine adds as 
a dependency. This is shared with [Marten](https://martendb.io) as well.
:::

To preview the generated source code, use this command line usage from the root directory of your .NET project:

```bash
dotnet run -- codegen preview
```

## Generating Code Ahead of Time

To write the source code ahead of time into your project, use:

```bash
dotnet run -- codegen write
```

This command **should** write all the source code files for each message handler and/or HTTP endpoint handler to `/Internal/Generated/WolverineHandlers`
directly under the root of your project folder.

## Optimized Workflow

::: info
Optimized Workflow overrides the storage migration [AutoBuildMessageStorageOnStartup](./durability/managing#disable-automatic-storage-migration) option, making it enabled for "Development" environment and disabled for other environments
:::

Or as a short hand option, use this:

<!-- snippet: sample_use_optimized_workflow -->
<a id='snippet-sample_use_optimized_workflow'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use "Auto" type load mode at development time, but
        // "Static" any other time
        opts.OptimizeArtifactWorkflow();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L63-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_optimized_workflow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which will use:

1. `TypeLoadMode.Auto` when the .NET environment is "Development" and try to write new source code to file
2. `TypeLoadMode.Static` for other .NET environments for optimized cold start times


