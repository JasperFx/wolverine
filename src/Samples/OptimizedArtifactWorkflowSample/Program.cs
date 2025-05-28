using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
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

        opts.Services.CritterStackDefaults(x =>
        {
            x.Production.GeneratedCodeMode = TypeLoadMode.Static;
            x.Production.ResourceAutoCreate = AutoCreate.None;

            // Little draconian, but this might be helpful
            x.Production.AssertAllPreGeneratedTypesExist = true;

            // These are defaults, but showing for completeness
            x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
            x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
        });
    }).RunWolverineAsync(args);