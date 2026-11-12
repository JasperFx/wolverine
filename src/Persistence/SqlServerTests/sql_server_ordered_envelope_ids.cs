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
        Envelope.IdGenerator = Guid.CreateVersion7;
    }

    private static readonly System.Reflection.MethodInfo SqlServerOrder =
        typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextGuid), Type.EmptyTypes)!;

    // GH-5197. GuidV7 is the 7.0 default
    private static readonly System.Reflection.MethodInfo DefaultOrder =
        typeof(Guid).GetMethod(nameof(Guid.CreateVersion7), Type.EmptyTypes)!;

    private static readonly System.Reflection.MethodInfo NewIdOrder =
        typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextSequentialGuid), Type.EmptyTypes)!;

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
                // their envelopes in PostgreSQL, so use the time-ordered GuidV7 ids
                opts.EnvelopeIdGeneration = EnvelopeIdGeneration.GuidV7;

                #endregion

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.GuidV7);
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

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.GuidV7);
        Envelope.IdGenerator.Method.ShouldBe(DefaultOrder);
    }

    // GH-5197. RestoreV6Defaults() brings back NewId as the plain default, but a SQL Server-only application still
    // gets SQL Server-ordered ids, as it did in 6.x
    [Fact]
    public async Task a_sql_server_only_application_keeps_sql_server_ordered_ids_after_restoring_the_v6_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.RestoreV6Defaults();
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        Envelope.IdGenerator.Method.ShouldBe(SqlServerOrder);
    }

    [Fact]
    public async Task opting_out_after_restoring_the_v6_defaults_keeps_new_id()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: false, schema: "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();

                // Called after the overload on purpose: the opt-out keeps whatever the default turns out to be
                opts.RestoreV6Defaults();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(NewIdOrder);
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
                opts.EnvelopeIdGeneration = EnvelopeIdGeneration.NewId;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString,
                    optimizeEnvelopeIdsForSqlServer: false, schema: "ordered_ids");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().EnvelopeIdGeneration
            .ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(NewIdOrder);
    }
}
