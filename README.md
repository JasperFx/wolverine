Jasper
======

[![Join the chat at https://gitter.im/JasperFx/jasper](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/JasperFx/jasper?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)
[![Build status](https://ci.appveyor.com/api/projects/status/o23fp3diks7024x9?svg=true)](https://ci.appveyor.com/project/jasper-ci/jasper) [![Join the chat at https://gitter.im/JasperFx/Wolverine](https://badges.gitter.im/JasperFx/Wolverine.svg)](https://gitter.im/JasperFx/Wolverine?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)


The [documentation is published here](http://jasperfx.github.io/).

Jasper is a next generation application development framework for distributed server side development in .NET. At the moment, Jasper can be used as:

1. An in-memory command runner
1. A robust, but lightweight asynchronous messaging framework (call it a service bus if you have to, but know that there's no centralized broker)
1. An alternative for authoring HTTP services within ASP.NET Core
1. A dessert topping (just kidding)

In all cases, Jasper can be used by itself or as an addon to an ASP.NET Core application. As much as possible, Jasper tries to leverage existing ASP.NET Core infrastructure.


## Working with the Code

The main solution file is `Jasper.sln`, and you should be good to go to simply open the code up in Rider, Visual Studio.NET, or VS Code and just go. In its current form, all the integration tests, including the Storyteller specifications, require [Docker](https://www.docker.com/) to be running on your development machine. For the docker dependencies (Postgresql, Rabbit MQ, Sql Server, etc.), run:

```bash
docker compose up -d
```

At the command line, the build script can be executed:

* on Windows with `build [task]`
* with Powershell with `./build.sh [task]`
* on *nix with `./build.sh [task]`

The default build task will run the basic unit tests along with the *Jasper.Http* tests. These other tasks may be helpful:

* `test-persistence` - runs the *Jasper.Persistence.Tests*
* `test-rabbitmq` - runs the Rabbit MQ transport tests
* `test-pulsar` - runs the Pulsar transport tests
* `test-tcp` - runs the TCP transport tests
* `storyteller`- runs the Storyteller specifications (think big, slow integration tests)
* `open_st` - opens the Storyteller UI to edit specifications or run interactively
* `full` - runs all possible tests. Get a cup of coffee or maybe just go out to lunch or the gym after you start this.


## What's with the name?

I think that FubuMVC turned some people off by its name ("for us, by us"). This time around I was going for an
unassuming name that was easy to remember and just named it after my (Jeremy) hometown (Jasper, MO).

## Implementing Jasper Transports

First off, the Rabbit MQ transport is the most mature of all the Jasper transport types, and is somewhat the template for new transports.

The basic steps:

1. If at all possible (i.e., anything but Azure Service Bus), add a docker container to the `docker-compose.yaml` file for the server piece of the new transport
   for local testing
1. Start a new project named *Jasper.{transport name}* and a matching *Jasper.{transport name}.Testing* project under the `/src` folder of the repository,
  but under the logical `/Transports` folder of the solution please.

1. Add a project reference to Jasper itself and a Nuget reference to the .NET adapter library for that transport. E.g., *DotPulsar* or *RabbitMQ.Client*
1. You'll need a custom subclass for `Endpoint` that represents either an address you're publishing to and/or listening for incoming messages. This will need to parse a custom Uri
  structure for the transport that identifies the transport type (the Uri scheme) and common properties like queue or topic names. See [RabbitMqEndpoint](https://github.com/JasperFx/jasper/blob/master/src/Jasper.RabbitMQ/Internal/RabbitMqEndpoint.cs) for an example
1. Add a custom implementation of the `IListener` interface
1. Add a custom implementation of the `ISender` interface
1. Implement the `ITransport` interface. See [RabbitMqTransport](https://github.com/JasperFx/jasper/blob/master/src/Jasper.RabbitMQ/Internal/RabbitMqTransport.cs) as an example.
  Any transport specific configuration should be properties of the concrete type. It's most likely useful to use the `TransportBase<T>` type
  as the base type, where `T` is the `Endpoint` type

1. Pair the custom `ITransport` type with a `Configure{tansport name}()` extension method on `IEndpoints` like [ConfigureRabbitMq()](https://github.com/JasperFx/jasper/blob/master/src/Jasper.RabbitMQ/RabbitMqTransportExtensions.cs#L36-L39)
  that let's the user configure transport specific capabilities and connectivity. We're working on the assumption that a single Jasper app will only connect to one broker
  for each transport type for now
1. For custom endpoint configuration for the new transport type, see the usage of `RabbitMqSubscriberConfiguration` and `RabbitMqListenerConfiguration`




