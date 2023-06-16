# AppWithMiddleware

This example demonstrates the usage of custom Wolverine middleware. This sample project was featured in [Introducing Wolverine for Effective Server Side .NET Development](https://jeremydmiller.com/2022/12/12/introducing-wolverine-for-effective-server-side-net-development/).

To run this example do the following:

1. Clone the Wolverine repo locally
1. From the root of the Wolverine repository run Postgres and PgAdmin4
   `docker compose up -d`
1. From within this folder run the application with
   `dotnet run` (or use Rider/Visual Studio.Net/IDE of your choice to run the project)
   Notice the output in the console. You will see that Marten and Wolverine create the initial schemas in the PostgreSQL
   database.

