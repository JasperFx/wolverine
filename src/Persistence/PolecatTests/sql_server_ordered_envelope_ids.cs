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
        Envelope.IdGenerator = NewId.NextSequentialGuid;
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
        Envelope.IdGenerator.Method.ShouldBe(typeof(NewId).GetMethod(nameof(NewId.NextGuid), Type.EmptyTypes));
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

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(typeof(NewId).GetMethod(nameof(NewId.NextSequentialGuid), Type.EmptyTypes));
    }
}
