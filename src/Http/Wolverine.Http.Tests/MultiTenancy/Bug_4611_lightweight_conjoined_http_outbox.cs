using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http.Tests.EfCoreOnly;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests.MultiTenancy;

/// <summary>
/// GH-4611, the HTTP half. <c>EFCorePersistenceFrameProvider.ApplyTransactionSupport</c> handled a
/// multi-tenanted DbContext only in its <c>Eager</c> branch, and the GH-3291 fix that enlists an HTTP
/// endpoint's DbContext in the outbox was explicitly skipped when <c>isMultiTenanted(...)</c> -- so a
/// Lightweight conjoined endpoint got neither the tenant-pinned DbContext nor the outbox, and its
/// cascaded messages were dispatched before <c>SaveChangesAsync</c> committed.
///
/// Like the sibling GH-3291 / GH-3353 / GH-3358 reproducers, the outbox half is asserted at the codegen
/// surface: the runtime symptom (a message delivered even though the save threw) races the durability
/// agent. The tenant-pinning half is asserted at runtime, where it is fully deterministic.
/// </summary>
public class Bug_4611_lightweight_conjoined_http_outbox : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS conjoined_http CASCADE";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddWolverineHttp();

        builder.Services.AddDbContextWithWolverineIntegration<Bug3353DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedNotesDbContext>(
            (options, connectionString) => options.UseNpgsql(connectionString.Value),
            AutoCreate.CreateOrUpdate);

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(ConjoinedNotesEndpoint).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_lw_http_wolverine");

            // The one difference from conjoined_tenancy_http_detection
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.Services.AddResourceSetupOnStartup();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(ConjoinedNoteCreatedHandler));
        });

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(x => x.TenantId.IsRequestHeaderValue("tenant"));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task lightweight_conjoined_endpoint_pins_the_dbcontext_to_the_detected_tenant()
    {
        var note = new CreateNote(Guid.NewGuid(), "red thoughts");

        await theHost.Scenario(x =>
        {
            x.Post.Json(note).ToUrl("/conjoined/notes/cascade");
            x.WithRequestHeader("tenant", "red");
            x.StatusCodeShouldBe(200);
        });

        // Pre-fix the row was stamped with the *DEFAULT* sentinel, so every tenant could read it
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select tenant_id from conjoined_http.tenanted_notes where \"Id\" = $1";
        cmd.Parameters.AddWithValue(note.Id);
        var storedTenant = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        storedTenant.ShouldBe("red");

        var blueNotes = await theHost.GetAsJson<TenantedNote[]>("/conjoined/notes?tenant=blue");
        blueNotes.ShouldNotBeNull();
        blueNotes.ShouldNotContain(x => x.Id == note.Id);
    }

    [Fact]
    public async Task lightweight_conjoined_endpoint_enlists_its_cascades_in_the_outbox()
    {
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/conjoined/notes/cascade");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, theHost.Services);
        var source = chain.SourceCode;
        source.ShouldNotBeNull();

        // The tenant-pinned DbContext comes from IDbContextBuilder<T>.BuildAndEnrollAsync(), which is
        // also what enlists the MessageContext in the outbox. Pre-fix the Lightweight chain service
        // located the DbContext instead, so neither happened.
        source.ShouldContain("BuildAndEnrollAsync");

        var buildAt = source.IndexOf(".BuildAndEnrollAsync(", StringComparison.Ordinal);
        var saveAt = source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var flushAt = source.IndexOf(".FlushOutgoingMessagesAsync(", StringComparison.Ordinal);

        buildAt.ShouldBeGreaterThanOrEqualTo(0);
        saveAt.ShouldBeGreaterThan(buildAt, "SaveChangesAsync must run after the tenant DbContext is enrolled");
        flushAt.ShouldBeGreaterThan(saveAt,
            "The outbox flush must run after SaveChangesAsync, or a failed save still delivers the cascade");
    }
}

// A routed handler so the cascaded NoteCreated has somewhere to go on the durable local queue
public static class ConjoinedNoteCreatedHandler
{
    public static void Handle(NoteCreated _)
    {
    }
}
