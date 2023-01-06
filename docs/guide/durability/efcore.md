# Entity Framework Core Integration

Wolverine supports [Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/) through the `WolverineFx.EntityFrameworkCore` Nuget.
There's only a handful of touch points to EF Core that you need to be aware of:

* Transactional middleware
* EF Core as a saga storage mechanism
* Outbox integration 

## Outbox Support

TODO -- update the sample projects with a Sql Server version. Show transactional usage. Outbox usage. Saga usage.
TODO -- update the sample projects with EF Core + Postgresql

## Outbox Outside of Wolverine Handlers

