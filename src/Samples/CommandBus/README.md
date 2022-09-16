# Command bus sample

This example demonstrates the use of Jasper in combination with Marten as a command bus.

To run this example do the following:

1. Clone the Jasper repo locally
1. From the root of the Jasper respository run Postgres and PgAdmin4
   `docker compose up postgresql pgadmin4 -d`
1. From within this folder run the application with
   `dotnet run`
   Notice the output in the console. You will see that Marten and Jasper create the initial schemas in the PostgreSQL
   database.
1. You can use PgAdmin4 to inspect your database. Access the management view on:
   `localhost:80`
   and use username: user@domain.com and password: SuperSecret
1. Use `curl` or Postman to create a reservation in the system. The POST request is as follows:

  ```
  URL: localhost:<port>/reservations
  METHOD: POST
  HEADERS: content-type: application/json
  BODY: { restaurantName: "Outpost", time: "2022-06-24T18:30:00.000z" }
  ```

Observe the output in the terminal when executing this POST request. Also use PgAdmin4 to double check that a
reservation has been added to the system

1. Use again
   `curl` or Postman to confirm the reservation in the system. The POST request is as follows:

  ```
  URL: localhost:<port>/reservations/confirm
  METHOD: POST
  HEADERS: content-type: application/json
  BODY: { reservationId: <Guid> }
  ```

where `<Guid>` is the Id of the reservation created in the DB (look it up via PgAdmin4).
