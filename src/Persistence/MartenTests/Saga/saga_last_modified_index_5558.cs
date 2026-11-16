using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Marten.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;
using Xunit;

namespace MartenTests.Saga;

// GH-5558: Wolverine maps Saga.LastModified onto Marten's mt_last_modified column, so the index for idle saga
// queries is Marten's own last-modified index
public class saga_last_modified_index_5558 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<IdleDiagnosticsSaga>();

                #region sample_marten_saga_last_modified_index
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_idle_index";

                    // Saga.LastModified is stored in Marten's mt_last_modified column, so index that
                    // column to find idle sagas without scanning the saga table
                    m.Schema.For<IdleDiagnosticsSaga>().IndexLastModified();
                }).IntegrateWithWolverine();
                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_saga_table_has_an_index_on_mt_last_modified()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var table = store.Options.FindOrResolveDocumentType(typeof(IdleDiagnosticsSaga)).TableName;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "select count(*) from pg_indexes where schemaname = :schema and tablename = :table and indexdef like '%mt_last_modified%'",
            conn);
        cmd.Parameters.AddWithValue("schema", table.Schema);
        cmd.Parameters.AddWithValue("table", table.Name);

        var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        count.ShouldBe(1);
    }

    [Fact]
    public async Task an_idle_saga_query_filters_on_mt_last_modified()
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();

        var command = session.Query<IdleDiagnosticsSaga>()
            .Where(x => x.LastModified < DateTimeOffset.UtcNow)
            .ToCommand(FetchType.Count);

        command.CommandText.ShouldContain("mt_last_modified");
    }
}
