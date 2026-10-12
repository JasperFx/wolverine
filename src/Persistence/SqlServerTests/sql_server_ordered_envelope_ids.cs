using IntegrationTests;
using JasperFx.Resources;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.SqlServer;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// GH-4902. A SQL Server-only application switches to SQL Server-ordered envelope ids at startup on its own; the
/// overloads force it or opt out of it; an explicit EnvelopeIdGeneration always wins. No schema change.
/// </summary>
public class sql_server_ordered_envelope_ids : IDisposable
{
    public void Dispose()
    {
        // The id generator is static, so put the default back for every other test in this assembly
        Envelope.IdGenerator = NewId.NextSequentialGuid;
    }

    private static readonly System.Reflection.MethodInfo SqlServerOrder =
        typeof(NewId).GetMethod(nameof(NewId.NextGuid), Type.EmptyTypes)!;

    private static readonly System.Reflection.MethodInfo DefaultOrder =
        typeof(NewId).GetMethod(nameof(NewId.NextSequentialGuid), Type.EmptyTypes)!;

    [Fact]
    public async Task a_sql_server_only_application_switches_automatically()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sql_server_only_application_gets_sql_server_ordered_ids

                // SQL Server is the only message storage, so Wolverine switches to
                // EnvelopeIdGeneration.NewIdSqlServerOrder at startup all by itself
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ordered_ids");

                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        Envelope.IdGenerator.Method.ShouldBe(SqlServerOrder);
    }

    [Fact]
    public async Task the_sql_server_transport_switches_automatically_too()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        Envelope.IdGenerator.Method.ShouldBe(SqlServerOrder);
    }

    [Fact]
    public async Task an_explicit_choice_beats_the_automatic_switch()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_opting_out_of_sql_server_ordered_ids_explicitly

                // This application sends messages to Wolverine services that store
                // their envelopes in PostgreSQL, so keep the default ids
                opts.EnvelopeIdGeneration = EnvelopeIdGeneration.NewId;

                #endregion

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(DefaultOrder);
    }

    [Fact]
    public async Task the_overload_can_opt_out_of_the_automatic_switch()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_persist_messages_with_sql_server_opting_out_of_sql_server_ordered_ids

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: false, schema: "ordered_ids");

                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(DefaultOrder);
    }

    [Fact]
    public async Task the_overload_can_force_sql_server_ordered_ids()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_persist_messages_with_sql_server_and_sql_server_ordered_ids

                // Forces SQL Server-ordered ids even when another message store has no preference
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: true, schema: "ordered_ids");

                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        Envelope.IdGenerator.Method.ShouldBe(SqlServerOrder);
    }

    [Fact]
    public async Task the_transport_overload_can_opt_out_too()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sql_server_persistence_and_transport_and_sql_server_ordered_ids

                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: false, schema: "ordered_ids");

                #endregion

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        Envelope.IdGenerator.Method.ShouldBe(DefaultOrder);
    }

    [Fact]
    public async Task false_leaves_an_explicit_choice_alone()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.EnvelopeIdGeneration = EnvelopeIdGeneration.GuidV7;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: false, schema: "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.GuidV7);
    }
}
