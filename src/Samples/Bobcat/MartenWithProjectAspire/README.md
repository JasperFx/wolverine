# MartenWithProjectAspire

This sample project just shows the mechanics of running PostgreSQL and Marten from a Project Aspire runner, and previews the Open Telemetry and metrics support introduced in Marten 7.10 using
the Project Aspire dashboard.

In this solution, you'll find four projects:

1. `TripDomain` -- just a shared library of domain types
1. `EventPublisher` -- a headless console application that continuously appends random events representing a `Trip` aggregate
1. `TripBuildingService` -- a headless console application that is running Marten's async daemon to continuously build projected documents from incoming events to the Marten event store
1. `AspireHost` -- a Project Aspire host project that will launch a new PostgreSQL container in Docker, pass the necessary connection string to the other applications, and start up `EventPublisher` and `TripBuildingService`

To run this sample project, you will first need to:

* Install [Project Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/setup-tooling) on your box and any dependencies like [Docker Desktop](https://www.docker.com/products/docker-desktop/) 
* Run the `AspireHost` project, either within JetBrains Rider / Visual Studio.Net, or through `dotnet run` in that project's root GetPackageDirectory
* Open your browser to the Aspire dashboard at [http://localhost:15242](http://localhost:15242)