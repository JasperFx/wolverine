using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Bugs;

#region Test Infrastructure

public interface IAncillaryStore2669 : IDocumentStore;

public record DispatchAncillaryWorkFromMain2669(Guid Id);

public record AncillaryWorkFromMain2669(Guid Id);

public static class DispatchAncillaryWorkFromMain2669Handler
{
    public static AncillaryWorkFromMain2669 Handle(DispatchAncillaryWorkFromMain2669 message)
    {
        return new AncillaryWorkFromMain2669(message.Id);
    }
}

[MartenStore(typeof(IAncillaryStore2669))]
public static class AncillaryWorkFromMain2669Handler
{
    public static IMartenOp Handle(AncillaryWorkFromMain2669 message)
    {
        return MartenOps.Store(new AncillaryWorkDocument2669 { Id = message.Id });
    }
}

public class AncillaryWorkDocument2669
{
    public Guid Id { get; set; }
}

#endregion

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2669.
///
/// A durable local message published from a main-store handler can be handled
/// transactionally by an ancillary Marten store. The receiving handler's
/// ancillary store should own the inbox row, even when the envelope was
/// originally stamped by the publishing context.
/// </summary>
public class Bug_2669_ancillary_marten_store_local_message_from_main_store : IAsyncLifetime
{
    private IHost _host = null!;
    private string _mainConnectionString = null!;
    private string _ancillaryConnectionString = null!;
    private static readonly string TargetFrameworkSuffix = AppContext.TargetFrameworkName?
        .Split("Version=v")
        .LastOrDefault()?
        .Replace(".", "_")
        .ToLowerInvariant() ?? "default";

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        _mainConnectionString = await CreateDatabaseIfNotExists(conn, $"bug_ancillary_from_main_{TargetFrameworkSuffix}");
        _ancillaryConnectionString = await CreateDatabaseIfNotExists(conn, $"bug_ancillary_from_main_refs_{TargetFrameworkSuffix}");

        await ResetSchema(_mainConnectionString, "public");
        await ResetSchema(_ancillaryConnectionString, "public");
        await ResetSchema(_ancillaryConnectionString, "organizations");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(_mainConnectionString);
                    m.DatabaseSchemaName = "public";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IAncillaryStore2669>(m =>
                {
                    m.Connection(_ancillaryConnectionString);
                    m.DatabaseSchemaName = "organizations";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.SchemaName = "organizations");

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DispatchAncillaryWorkFromMain2669Handler))
                    .IncludeType(typeof(AncillaryWorkFromMain2669Handler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task durable_local_message_from_main_store_can_be_handled_by_ancillary_marten_store()
    {
        var id = Guid.NewGuid();

        await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(new DispatchAncillaryWorkFromMain2669(id));

        await using var session = _host.Services
            .GetRequiredService<IAncillaryStore2669>()
            .LightweightSession();

        var document = await session.LoadAsync<AncillaryWorkDocument2669>(id);
        document.ShouldNotBeNull();
    }

    private static async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            try
            {
                await new DatabaseSpecification().BuildDatabase(conn, databaseName);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Parallel target framework runs can race to create the same database.
            }
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    private static async Task ResetSchema(string connectionString, string schemaName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync(schemaName);

        if (schemaName == "public")
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "create schema if not exists public;";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
