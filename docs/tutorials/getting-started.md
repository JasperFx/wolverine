# Getting Started

Wolverine is a toolset for command execution and message handling within .NET applications.
The killer feature of Wolverine (we think) is its very efficient command execution pipeline that
can be used as:

1. An [inline "mediator" pipeline](/tutorials/mediator) for executing commands
2. A [local message bus](/guide/messaging/transports/local) for in-application communication
3. A full-fledged [asynchronous messaging framework](/guide/messaging/introduction) for robust communication and interaction between services when used in conjunction with low level messaging infrastructure tools like RabbitMQ
4. With the [WolverineFx.Http](/guide/http/) library, Wolverine's execution pipeline can be used directly as an alternative ASP.Net Core Endpoint provider

Wolverine tries very hard to be a good citizen within the .NET ecosystem. Even when used in
"headless" services, it uses the idiomatic elements of .NET (logging, configuration, bootstrapping, hosted services)
rather than try to reinvent something new. Wolverine utilizes the [.NET Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host) for bootstrapping and application teardown.
This makes Wolverine relatively easy to use in combination with many of the most popular .NET tools.

## Your First Wolverine Application

Also see the full [quickstart code](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Quickstart) on GitHub.

For a first application, let's say that we're building a very simple issue tracking system for
our own usage. If you're reading this web page, it's a pretty safe bet you spend quite a bit of time
working with an issue tracking system. :)

Ignoring any discussion of the user interface or even a backing database, let's
start a new web api project for this new system with:

```bash
dotnet new webapi
```
Next, let's add Wolverine to our project with:

```bash
dotnet add package WolverineFx
```

To start off, we're just going to build two API endpoints that accepts
a POST from the client that...

1. Creates a new `Issue`, stores it, and triggers an email to internal personnel.
2. Assigns an `Issue` to an existing `User` and triggers an email to that user letting them know there's more work on their plate

The two *commands* for the POST endpoints are below:

<a id='#region sample_Quickstart_commands_CreateIssue'></a>
```cs
public record CreateIssue(Guid OriginatorId, string Title, string Description);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/CreateIssue.cs#L3-L7' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_commands' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and

<!-- snippet: sample_Quickstart_commands_AssignIssue -->
<a id='snippet-sample_quickstart_commands_assignissue'></a>
```cs
public record AssignIssue(Guid IssueId, Guid AssigneeId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/AssignIssue.cs#L3-L7' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_commands_assignissue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Let's jump right into the `Program.cs` file of our new web service:

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

::: tip
`IMessageBus` is the entrypoint into all message invocation, publishing, or scheduling. Pretty much everything at runtime will start with this service. Wolverine
registers `IMessageBus` as a scoped service inside your application's DI container as part of the `UseWolverine()` mechanism.
:::

Alright, let's talk about what we wrote up above:

1. We integrated Wolverine into the new system through the call to `IHostBuilder.UseWolverine()`
2. We registered the `UserRepository` and `IssueRepository` services
3. We created a couple [Minimal API](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-6.0) endpoints

See also: [Wolverine as Command Bus](/guide/in-memory-bus)

The two Web API functions directly delegate to Wolverine's `IMessageBus.InvokeAsync()` method.
In that method, Wolverine will direct the command to the correct handler and invoke that handler
inline. In a simplistic form, here is the entire handler file for the `CreateIssue`
command:

<!-- snippet: sample_Quickstart_CreateIssueHandler -->
<a id='snippet-sample_quickstart_createissuehandler'></a>
```cs
namespace Quickstart;

public class CreateIssueHandler
{
    private readonly IssueRepository _repository;

    public CreateIssueHandler(IssueRepository repository)
    {
        _repository = repository;
    }

    // The IssueCreated event message being returned will be
    // published as a new "cascaded" message by Wolverine after
    // the original message and any related middleware has
    // succeeded
    public IssueCreated Handle(CreateIssue command)
    {
        var issue = new Issue
        {
            Title = command.Title,
            Description = command.Description,
            IsOpen = true,
            Opened = DateTimeOffset.Now,
            OriginatorId = command.OriginatorId
        };

        _repository.Store(issue);

        return new IssueCreated(issue.Id);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/CreateIssueHandler.cs#L1-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_createissuehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Hopefully that code is simple enough, but let's talk what you do not see in this code or
the initial `Program` code up above.

Wolverine uses a [naming convention](/guide/handlers/#rules-for-message-handlers) to automatically discover message handler actions in your
application assembly, so at no point did we have to explicitly register the
`CreateIssueHandler` in any way.

We didn't have to use any kind of base class, marker interface, or .NET attribute to designate
any part of the behavior of the `CreateIssueHandler` class. In the `Handle()` method, the
first argument is always assumed to be the message type for the handler action. It's not apparent
in any of the quick start samples, but Wolverine message handler methods can be asynchronous as
well as synchronous, depending on what makes sense in each handler. So no littering your code
with extraneous `return Task.Completed;` code like you'd have to with other .NET tools.

::: info
These conventions are just some of the ways Wolverine keeps out of the way of your application code whilst
enabling developers to write more concise, decoupled code.
:::

As mentioned earlier, we want our API to create an email whenever a new issue is created. In
this case we're opting to have that email generation and email sending happen in a second
message handler that will run after the initial command. You might also notice that the `CreateIssueHandler.Handle()` method returns an `IssueCreated` event.
When Wolverine sees that a handler creates what we call a [cascading message](/guide/handlers/cascading), Wolverine will
publish the `IssueCreated` event to an in memory
queue after the initial message handler succeeds. The advantage of doing this is allowing the
slower email generation and sending process to happen in background processes instead of holding up
the initial web service call.

The `IssueHandled` event message will be handled by this code:

<!-- snippet: sample_Quickstart_IssueCreatedHandler -->
<a id='snippet-sample_quickstart_issuecreatedhandler'></a>
```cs
public static class IssueCreatedHandler
{
    public static async Task Handle(IssueCreated created, IssueRepository repository)
    {
        var issue = repository.Get(created.Id);
        var message = await BuildEmailMessage(issue);
        using var client = new SmtpClient();
        client.Send(message);
    }

    // This is a little helper method I made public
    // Wolverine will not expose this as a message handler
    internal static Task<MailMessage> BuildEmailMessage(Issue issue)
    {
        // Build up a templated email message, with
        // some sort of async method to look up additional
        // data just so we can show off an async
        // Wolverine Handler
        return Task.FromResult(new MailMessage());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/IssueCreatedHandler.cs#L5-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_issuecreatedhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, you'll notice that Wolverine is happy to allow you to use static methods as
handler actions. And also notice that the `Handle()` method takes in an argument
for `IssueRepository`. Wolverine always assumes that the first argument of a handler
method is the message type, but other arguments are inferred to be services from the
system's underlying IoC container. By supporting [method injection](https://www.tatvasoft.com/outsourcing/2023/11/dependency-injection-in-csharp.html#Method) like this, Wolverine
is able to cut down on even more of the typical cruft code forced upon you by other .NET tools.

*You might be saying that this sounds like the behavior of the conventional method injection
behavior of Minimal API in .NET 6, and it is. But we'd like to point out that Wolverine had this
years before the ASP.NET team got around to it. :-)*

This page introduced the basic usage of Wolverine, how to wire Wolverine
into .NET applications, and some rudimentary `Handler` usage. Of course, this all was
local with in memory usage and Wolverine can do so much more. Dive deeper and 
learn more about its other [Handlers and Messages](/guide/handlers/) capabilities.
