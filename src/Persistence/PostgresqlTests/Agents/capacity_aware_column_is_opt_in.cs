using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace PostgresqlTests.Agents;

/// <summary>
/// GH-3959. The load_factor column and every statement naming it are gated on
/// CapacityAwareAssignment, so upgrading without opting in migrates nothing and reads nothing.
/// </summary>
public class capacity_aware_column_is_opt_in : PostgresqlContext
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IHost> startAsync(string schema, bool capacityAware, AutoCreate autoCreate)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Durability.CapacityAwareAssignment = capacityAware;
                opts.AutoBuildMessageStorageOnStartup = autoCreate;

                if (autoCreate != AutoCreate.None)
                {
                    opts.Services.AddResourceSetupOnStartup();
                }
            }).StartAsync(Ct);
    }

    private static async Task<long> loadFactorColumnCountAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from information_schema.columns where table_schema = '{schema}' and table_name = 'wolverine_nodes' and column_name = 'load_factor'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private static async Task dropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Ct);
        await conn.DropSchemaAsync(schema, ct: Ct);
    }

    [Fact]
    public async Task no_column_and_a_working_agent_plane_when_the_flag_is_off()
    {
        const string schema = "capacity_off";
        await dropSchemaAsync(schema);

        var host = await startAsync(schema, capacityAware: false, AutoCreate.CreateOrUpdate);

        try
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

            (await runtime.Storage.Nodes.MarkHealthCheckAsync(WolverineNode.For(runtime.Options), Ct))
                .ShouldBeTrue();

            var nodes = await runtime.Storage.Nodes.LoadAllNodesAsync(Ct);
            nodes.ShouldNotBeEmpty();
            nodes.ShouldAllBe(x => x.LoadFactor == null);

            await runtime.Storage.Nodes.LoadNodeAgentStateAsync(Ct);
        }
        finally
        {
            await host.StopAsync(Ct);
            host.Dispose();
        }

        (await loadFactorColumnCountAsync(schema)).ShouldBe(0);
    }

    [Fact]
    public async Task the_column_is_provisioned_and_round_trips_a_reading_when_the_flag_is_on()
    {
        const string schema = "capacity_on";
        await dropSchemaAsync(schema);

        var host = await startAsync(schema, capacityAware: true, AutoCreate.CreateOrUpdate);

        try
        {
            (await loadFactorColumnCountAsync(schema)).ShouldBe(1);

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

            var node = WolverineNode.For(runtime.Options);
            node.LoadFactor = 42.5;
            (await runtime.Storage.Nodes.MarkHealthCheckAsync(node, Ct)).ShouldBeTrue();

            var reloaded = await runtime.Storage.Nodes.LoadAllNodesAsync(Ct);
            reloaded.Single(x => x.NodeId == runtime.Options.UniqueNodeId)
                .LoadFactor.ShouldBe(42.5);
        }
        finally
        {
            await host.StopAsync(Ct);
            host.Dispose();
        }
    }

    /// <summary>
    /// The case the gate exists for: an AutoCreate.None deployment that upgrades without opting in
    /// never gets the column provisioned, and must not be asked for it.
    /// </summary>
    [Fact]
    public async Task auto_create_none_against_a_database_without_the_column()
    {
        const string schema = "capacity_none";
        await dropSchemaAsync(schema);

        // Provision with the column, then drop it: that is exactly the pre-GH-3959 node table.
        var seed = await startAsync(schema, capacityAware: true, AutoCreate.CreateOrUpdate);
        await seed.StopAsync(Ct);
        seed.Dispose();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"alter table {schema}.wolverine_nodes drop column load_factor";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var host = await startAsync(schema, capacityAware: false, AutoCreate.None);

        try
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

            await runtime.Storage.Nodes.MarkHealthCheckAsync(WolverineNode.For(runtime.Options), Ct);
            await runtime.Storage.Nodes.LoadAllNodesAsync(Ct);
            await runtime.Storage.Nodes.LoadNodeAgentStateAsync(Ct);
        }
        finally
        {
            await host.StopAsync(Ct);
            host.Dispose();
        }

        (await loadFactorColumnCountAsync(schema)).ShouldBe(0);
    }
}
