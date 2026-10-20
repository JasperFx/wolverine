using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;

namespace MartenTests.Bugs;

/// <summary>
/// The Wolverine message store built by IntegrateWithWolverine() sits on Marten's own NpgsqlDataSource, and
/// MessageDatabase.DisposeAsync disposed it unconditionally. Disposing an NpgsqlDataSource aborts every physical
/// connection rented from it, so Wolverine's teardown broke a DocumentStore that was still in use, and disposed a
/// data source the application gave Marten through UseNpgsqlDataSource(), which Marten itself stopped disposing in
/// Marten #4874.
/// </summary>
public class Bug_message_store_disposes_martens_data_source : PostgresqlContext
{
    private const string SchemaName = "marten_ds_ownership";

    [Fact]
    public async Task stopping_the_host_does_not_dispose_a_data_source_given_to_marten()
    {
        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(dataSource);
                opts.Services.AddMarten(m =>
                {
                    m.DatabaseSchemaName = SchemaName;
                    m.DisableNpgsqlLogging = true;
                }).UseNpgsqlDataSource().IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        await using var command = dataSource.CreateCommand("select 1");
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task disposing_the_message_store_leaves_the_document_store_usable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = SchemaName;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.Services.GetRequiredService<IMessageStore>().DisposeAsync();

        // The data source is Marten's, and Marten is still running
        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new DataSourceOwnershipDoc { Id = Guid.NewGuid() });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public class DataSourceOwnershipDoc
{
    public Guid Id { get; set; }
}
