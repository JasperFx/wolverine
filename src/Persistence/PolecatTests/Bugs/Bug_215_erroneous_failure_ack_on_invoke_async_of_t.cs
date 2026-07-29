using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;

namespace PolecatTests.Bugs;

public class Bug_215_erroneous_failure_ack_on_invoke_async_of_t
{
    [Fact]
    public async Task no_failure_ack_on_invoke_async()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_215";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var data = new PcBug215Data();

        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(data);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var bus = host.MessageBus();
        var response = await bus.InvokeAsync<PcBug215Data>(new PcLookup(data.Id), TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
    }
}

public record PcLookup(Guid Id);

public class PcLookupHandler
{
    public static async Task<PcBug215Data?> Handle(PcLookup lookup, IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var data = new PcBug215Data();
        session.Store(data);

        return await session.LoadAsync<PcBug215Data>(lookup.Id, cancellationToken);
    }
}

public class PcBug215Data
{
    public Guid Id { get; set; }
}
