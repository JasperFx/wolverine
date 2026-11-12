using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;

namespace PostgresqlTests;

/// <summary>
/// GH-5197. PostgreSQL has no envelope id preference, so a PostgreSQL-only application gets the 7.0 default,
/// GuidV7, whose ids sort by time in a PostgreSQL <c>uuid</c> column. RestoreV6Defaults() brings back NewId.
/// </summary>
[Collection("marten")]
public class postgresql_only_application_uses_guid_v7_envelope_ids_5197 : IDisposable
{
    public void Dispose()
    {
        // The id generator is static, so put the default back for every other test in this assembly
        Envelope.IdGenerator = Guid.CreateVersion7;
    }

    [Fact]
    public async Task a_postgresql_only_application_uses_guid_v7()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "guid_v7_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.GuidV7);
        Envelope.IdGenerator.Method.ShouldBe(typeof(Guid).GetMethod(nameof(Guid.CreateVersion7), Type.EmptyTypes));
    }

    [Fact]
    public async Task a_postgresql_only_application_uses_new_id_after_restoring_the_v6_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.RestoreV6Defaults();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "guid_v7_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextSequentialGuid), Type.EmptyTypes));
    }
}
