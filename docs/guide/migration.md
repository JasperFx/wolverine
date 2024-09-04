# Migration Guide

## Key Changes in 3.0

The 3.0 release did not have any breaking changes to the public API, but does come with some significant internal
changes.

The biggest change is that Wolverine is no longer directly coupled to the [Lamar IoC library](https://jasperfx.github.io/lamar).
Wolverine will no longer automatically replace the built in `ServiceProvider` with Lamar. At this point it is theoretically
possible to use Wolverine with any IoC library that fully supports the ASP.Net Core DI conformance behavior, but Wolverine
has only been tested against the default `ServiceProvider` and Lamar IoC containers. 

Do be aware if moving to Wolverine 3.0 that Lamar is more forgiving than `ServiceProvider`, so there might be some hiccups
if you choose to forgo Lamar. See the [Configuration Guide](/guide/configuration) for more information.

Wolverine 3.0 can now be bootstrapped with the `HostApplicationBuilder` or any standard .NET bootstrapping mechanism through
`IServiceCollection.AddWolverine()`. The old limitation of having to use `IHostBuilder` is not gone.

Wolverine 3.0 added optimistic concurrency support to the stateful `Saga` support. This will potentially cause database
migrations for any Marten-backed `Saga` types as it will now require the numeric version storage.

The leader election functionality in Wolverine has been largely rewritten and *should* eliminate the issues with poor 
behavior in clusters or local debugging time usage where nodes do not gracefully shut down. Internal testing has shown
a significant improvement in Wolverine's ability to detect node changes and rollover the leadership election.

The PostgreSQL transport option requires you to explicitly set the `transportSchema`, or Wolverine will fall through to
using `wolverine_queues` as the schema for the database backed queues. Wolverine will no longer use the envelope storage
schema for the queues.

For [Wolverine.Http usage](/guide/http/), the Wolverine 3.0 usage of the less capable `ServiceProvider` instead of the previously
mandated [Lamar](https://jasperfx.github.io/lamar) library necessitates the usage of this API to register necessary
services for Wolverine.HTTP in addition to adding the Wolverine endpoints:

<!-- snippet: sample_adding_http_services -->
<a id='snippet-sample_adding_http_services'></a>
```cs
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Necessary services for Wolverine HTTP
// And don't worry, if you forget this, Wolverine
// will assert this is missing on startup:(
builder.Services.AddWolverineHttp();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L26-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_http_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Also for Wolverine.Http users, the `[Document]` attribute behavior in the Marten integration is now "required by default."

The behavior of `IMessageBus.InvokeAsync<T>(message)` changed in 3.0 such that the `T` response **is not also published as a 
message** at the same time when the initial message is sent with request/response semantics. Wolverine has gone back and forth
in this behavior in its life, but at this point, the Wolverine thinks that this is the least confusing behavioral rule. 

You can selectively override this behavior and tell Wolverine to publish the response as a message no matter what
by using the new 3.0 `[AlwaysPublishResponse]` attribute like this:

snippet: sample_using_AlwaysPublishResponse