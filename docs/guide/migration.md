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