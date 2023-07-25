# Working with Code Generation

::: tip
Note that you may need to delete the existing source code when you change
handler signatures or add or remove middleware. Nothing in Wolverine is able
to detect that the generated source code needs to be rewritten
:::

Wolverine uses runtime code generation to create the "adaptor" code that Wolverine uses to call into 
your message handlers. Wolverine's [middleware strategy](/guide/handlers/middleware) also uses this strategy to "weave" calls to 
middleware directly into the runtime pipeline without requiring the copious usage of adapter interfaces
that is prevalent in most other .NET frameworks.

That's great when everything is working as it should, but there's a couple issues:

1. There's a little bit of conventional magic in how Wolverine finds and applies middleware or passed arguments
   to your message handlers or HTTP endpoint handlers. 
2. The usage of the Roslyn compiler at runtime *can sometimes be slow* on its first usage. This can lead to sluggish *cold start*
   times in your application that might be problematic in serverless scenarios for examples. 

Not to worry though, Wolverine has several facilities to either preview the generated code for diagnostic purposes to 
really understand how Wolverine is interacting with your code and to optimize the "cold start" by generating the dynamic
code ahead of time so that it can be embedded directly into your application's main assembly and discovered from there.

Most of the facilities shown here will require the [Oakton command line integration](./command-line).

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L11-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_codegen_type_load_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L36-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_optimized_workflow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which will use:

1. `TypeLoadMode.Auto` when the .NET environment is "Development" and try to write new source code to file
2. `TypeLoadMode.Static` for other .NET environments for optimized cold start times
