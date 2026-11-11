using IntegrationTests;
using JasperFx.Resources;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Xunit;

namespace PolecatTests;

/// <summary>
/// GH-4902. Polecat keeps Wolverine's envelopes in SQL Server, so a Polecat-only application switches to SQL
/// Server-ordered envelope ids at startup on its own, and the IntegrateWithWolverine overload can opt out
/// </summary>
public class sql_server_ordered_envelope_ids : IDisposable
{
    public void Dispose()
    {
        // The id generator is static, so put the default back for every other test in this assembly
        Envelope.IdGenerator = Guid.CreateVersion7;
    }

    [Fact]
    public async Task a_polecat_only_application_switches_automatically()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat_ordered_ids";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        Envelope.IdGenerator.Method.ShouldBe(typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextGuid), Type.EmptyTypes));
    }

    // GH-5197. Polecat keeps SQL Server-ordered ids after RestoreV6Defaults() too
    [Fact]
    public async Task a_polecat_only_application_keeps_sql_server_ordered_ids_after_restoring_the_v6_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.RestoreV6Defaults();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat_ordered_ids";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        Envelope.IdGenerator.Method.ShouldBe(typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextGuid), Type.EmptyTypes));
    }

    [Fact]
    public async Task integrate_with_wolverine_can_opt_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_polecat_integration_with_sql_server_ordered_ids

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "polecat_ordered_ids";
                    })
                    // This application sends messages to Wolverine services that store their
                    // envelopes in PostgreSQL, so keep the default envelope ids
                    .IntegrateWithWolverine(optimizeEnvelopeIdsForSqlServer: false);

                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        // GH-5197. The default ids are GuidV7 in 7.0
        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.GuidV7);
        Envelope.IdGenerator.Method.ShouldBe(typeof(Guid).GetMethod(nameof(Guid.CreateVersion7), Type.EmptyTypes));
    }
}
