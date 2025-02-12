# Command Line Integration

With help from its [JasperFx](https://github.com/JasperFx) team mate [Oakton](https://jasperfx.github.io/oakton), Wolverine supports quite a few command line diagnostic and resource management
tools. To get started, apply Oakton as the command line parser in your applications as shown in the last line of code in this
sample application bootstrapping from Wolverine's [Getting Started](/tutorials/getting-started):

<!-- snippet: sample_Quickstart_Program -->
<a id='snippet-sample_quickstart_program'></a>
```cs
using JasperFx;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// The almost inevitable inclusion of Swashbuckle:)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// For now, this is enough to integrate Wolverine into
// your application, but there'll be *many* more
// options later of course :-)
builder.Host.UseWolverine();

// Some in memory services for our application, the
// only thing that matters for now is that these are
// systems built by the application's IoC container
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IssueRepository>();

var app = builder.Build();

// An endpoint to create a new issue that delegates to Wolverine as a mediator
app.MapPost("/issues/create", (CreateIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// An endpoint to assign an issue to an existing user that delegates to Wolverine as a mediator
app.MapPost("/issues/assign", (AssignIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// Swashbuckle inclusion
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger"));

// Opt into using JasperFx for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/Program.cs#L1-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_program' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From this project's root in the command line terminal tool of your choice, type:

```bash
dotnet run -- help
```

and you *should* get this hopefully helpful rundown of available command options:

```bash
The available commands are:
                                                                                                    
  Alias       Description                                                                           
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  check-env   Execute all environment checks against the application                                
  codegen     Utilities for working with JasperFx.CodeGeneration and JasperFx.RuntimeCompiler       
  describe    Writes out a description of your running application to either the console or a file  
  help        List all the available commands                                                       
  resources   Check, setup, or teardown stateful resources of this system                           
  run         Start and run this .Net application                                                   
  storage     Administer the Wolverine message storage                                                       
                                                                                                    

Use dotnet run -- ? [command name] or dotnet run -- help [command name] to see usage help about a specific command

```

## Describe a Wolverine Application

::: tip
While Wolverine certainly knows upfront what message types it handles, you may need to help Wolverine "know" what types
will be outgoing messages later with the [message discovery](/guide/messages.html#message-discovery) support.
:::

Wolverine is admittedly a configuration-heavy framework, and some combinations of conventions, policies, and explicit configuration
could easily lead to confusion about how the system is going to behave. To help ameliorate that possible situation -- but also to help the 
Wolverine team be able to remotely support folks using Wolverine -- you have this command line tool:

```bash
dotnet run -- describe
```

At this time, a Wolverine application will spit out command line reports about its configuration that
will describe:

* "Wolverine Options" - the basics properties as configured, including what Wolverine thinks is the application assembly and any registered extensions
* "Wolverine Listeners" - a tabular list of all the configured listening endpoints, including local queues, within the system and information about how they are configured
* "Wolverine Message Routing" - a tabular list of all the message routing for *known* messages published within the system
* "Wolverine Sending Endpoints" - a tabular list of all *known*, configured endpoints that send messages externally
* "Wolverine Error Handling" - a preview of the active message failure policies active within the system
* "Wolverine Http Endpoints" - shows all Wolverine HTTP endpoints. This is only active if WolverineFx.HTTP is used within the system

## Other Highlights

* See the [code generation support](./codegen)
* The `storage` command helps manage the [durable messaging support](./durability/)
* Wolverine has direct support for [Oakton](https://jasperfx.github.io/oakton) environment checks and resource management that
  can be very helpful for Wolverine integrations with message brokers or database servers





