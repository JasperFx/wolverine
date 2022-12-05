using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;

return await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "optimized";
        }).IntegrateWithWolverine();

        opts.Services.AddHostedService<MessageSender>();
        opts.PublishMessage<TrackedMessage>()
            .ToLocalQueue("tracked")
            .UseDurableInbox();

        opts.OptimizeArtifactWorkflow();
    }).RunWolverineAsync(args);