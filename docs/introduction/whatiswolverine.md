# What is Wolverine?

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


## .NET Version Compatibility

Wolverine aligns with the [.NET Core Support Lifecycle](https://dotnet.microsoft.com/platform/support/policy/dotnet-core) to determine platform support. New major releases will drop versions of .NET that have fallen out of support.
