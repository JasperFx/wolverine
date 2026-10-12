using Fisher;
using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Polecat;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.TenantedActs;

// GH-4962 phase 1: an act carries its tenant -- from the entry message's envelope here, from an HTTP endpoint's
// tenant detection in Wolverine.Bobcat.Http.Tests -- and on a conjoined store (one database, the tenant as a column)
// ThenEventuallyCaused queries the act's correlation id within that tenant only. On Marten, Polecat and Fisher.

public record ShipCrate(Guid CrateId);
public record CrateShipped(Guid CrateId);

public class Crate
{
    public Guid Id { get; set; }
    public static Crate Create(CrateShipped e) => new();
}

public static class ShipCrateHandler
{
    public static StartStream Handle(ShipCrate command)
        => Storage.StartStream<Crate>(command.CrateId, new CrateShipped(command.CrateId));
}

public abstract class ConjoinedHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task BeforeStartAsync() => Task.CompletedTask;

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    /// <summary>
    /// Append <paramref name="e" /> to a new stream for <paramref name="tenantId" />, recorded with
    /// <paramref name="correlationId" />: another tenant's event that happens to carry the act's correlation id.
    /// </summary>
    public abstract Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e);

    public async ValueTask InitializeAsync()
    {
        await BeforeStartAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ShipCrateHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenConjoinedHost : ConjoinedHost
{
    protected override async Task BeforeStartAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var drop = conn.CreateCommand();
        drop.CommandText = "DROP SCHEMA IF EXISTS bobcat_tenanted_acts CASCADE;";
        await drop.ExecuteNonQueryAsync();
    }

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_tenanted_acts";
                m.DisableNpgsqlLogging = true;
                m.Events.TenancyStyle = TenancyStyle.Conjoined;
                m.Events.MetadataConfig.CorrelationIdEnabled = true;
                m.Policies.AllDocumentsAreMultiTenanted();
            })
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => Host.Services.GetRequiredService<global::Marten.IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    public override async Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e)
    {
        await using var session = Host.Services.GetRequiredService<global::Marten.IDocumentStore>().LightweightSession(tenantId);
        session.CorrelationId = correlationId;
        session.Events.StartStream<Crate>(streamId, e);
        await session.SaveChangesAsync();
    }
}

public sealed class PolecatConjoinedHost : ConjoinedHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "bobcat_tenanted_acts";
                m.Events.TenancyStyle = TenancyStyle.Conjoined;
                m.Events.EnableCorrelationId = true;
            })
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();

    public override async Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e)
    {
        await using var session = Host.Services.GetRequiredService<PolecatStore>().LightweightSession(tenantId);
        session.CorrelationId = correlationId;
        session.Events.StartStream<Crate>(streamId, e);
        await session.SaveChangesAsync();
    }
}

public sealed class FisherConjoinedHost : ConjoinedHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_tenanted_acts_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
                o.Events.TenancyStyle = TenancyStyle.Conjoined;
                o.Events.EnableCorrelationId = true;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

    public override async Task AppendForTenantAsync(string tenantId, string correlationId, Guid streamId, object e)
    {
        await using var session = Host.Services.GetRequiredService<global::Fisher.IDocumentStore>().LightweightSession(tenantId);
        session.CorrelationId = correlationId;
        session.Events.StartStream<Crate>(streamId, e);
        await session.SaveChangesAsync();
    }
}

public abstract class tenanted_acts<THost>(THost app) : WolverineSpec(app.Host) where THost : ConjoinedHost
{
    [Fact]
    public async Task a_message_act_carries_the_tenant_it_was_sent_for()
    {
        var theCrate = Guid.CreateVersion7();

        await WhenReceived(new ShipCrate(theCrate), options: new DeliveryOptions { TenantId = "red" });

        LastAct.TenantId.ShouldBe("red");
        LastAct.Appended.Single().TenantId.ShouldBe("red");
        await ThenEventuallyCaused(new CrateShipped(theCrate));
    }

    [Fact]
    public async Task an_act_with_no_tenant_carries_none()
    {
        var theCrate = Guid.CreateVersion7();

        await WhenReceived(new ShipCrate(theCrate), options: new DeliveryOptions { TenantId = "red" });
        LastAct.TenantId.ShouldNotBeNull();

        await WhenTracked("nothing happens", () => Task.CompletedTask);
        LastAct.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task another_tenants_event_with_the_same_correlation_id_is_not_the_acts()
    {
        Scenario.EventuallyTimeout = TimeSpan.FromMilliseconds(300);
        var theCrate = Guid.CreateVersion7();

        await WhenReceived(new ShipCrate(theCrate), options: new DeliveryOptions { TenantId = "red" });

        // A blue event recorded with the act's correlation id: on a conjoined store the query stays in red
        var blueCrate = Guid.CreateVersion7();
        await app.AppendForTenantAsync("blue", LastAct.CorrelationId!, blueCrate, new CrateShipped(blueCrate));

        await ThenEventuallyCaused(new CrateShipped(theCrate));

        var failure = await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused(new CrateShipped(blueCrate)));
        failure.Message.ShouldContain("for tenant 'red'");
    }
}

public sealed class marten_tenanted_acts(MartenConjoinedHost app)
    : tenanted_acts<MartenConjoinedHost>(app), IClassFixture<MartenConjoinedHost>;

public sealed class polecat_tenanted_acts(PolecatConjoinedHost app)
    : tenanted_acts<PolecatConjoinedHost>(app), IClassFixture<PolecatConjoinedHost>;

public sealed class fisher_tenanted_acts(FisherConjoinedHost app)
    : tenanted_acts<FisherConjoinedHost>(app), IClassFixture<FisherConjoinedHost>;
