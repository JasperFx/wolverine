var builder = DistributedApplication.CreateBuilder(args);

var postgresdb = builder.AddPostgres("marten");

builder.AddProject<Projects.EventPublisher>("publisher")
    .WithReference(postgresdb);

builder.AddProject<Projects.TripBuildingService>("trip-building")
    .WithReference(postgresdb);

builder.Build().Run();