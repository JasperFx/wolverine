using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.Bugs;

public class Bug_215_erroneous_failure_ack_on_invoke_async_of_t : PostgresqlContext
{
    [Fact]
    public async Task no_failure_ack_on_invoke_async()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bugs";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var data = new Bug215Data();

        using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(data);
            await session.SaveChangesAsync();
        }

        var bus = host.MessageBus();
        var response = await bus.InvokeAsync<Bug215Data>(new Lookup(data.Id));

        response.ShouldNotBeNull();
    }
}

public record Lookup(Guid Id);

public class LookupHandler
{
    public static async Task<Bug215Data> Handle(Lookup lookup, IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var data = new Bug215Data();
        session.Store(data);

        //await session.SaveChangesAsync(cancellationToken);

        return await session.LoadAsync<Bug215Data>(lookup.Id, cancellationToken);
    }
}

public class Bug215Data
{
    public Guid Id { get; set; }
}