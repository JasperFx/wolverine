# Command bus sample

This example demonstrates the use of Wolverine in combination with Marten as a command bus.

To run this example do the following:

1. Clone the Wolverine repo locally
1. From the root of the Wolverine repository run Postgres and PgAdmin4
   `docker compose up -d`
1. From within this folder run the application with
   `dotnet run` (or use Rider/Visual Studio.Net/IDE of your choice to run the project)
   Notice the output in the console. You will see that Marten and Wolverine create the initial schemas in the PostgreSQL
   database.

The browser should open to the swagger page
at [http://localhost:5193/swagger/index.html](http://localhost:5193/swagger/index.html) 

