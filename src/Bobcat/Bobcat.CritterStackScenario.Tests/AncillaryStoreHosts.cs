using Fisher;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#504: an application with a main event store and an ancillary one, on Marten and on Fisher. The main
// store holds the Wallet side of the domain, the ancillary store the Invoice side, and both register SharedNote
// so a step about it is ambiguous.

/// <summary>Marten's ancillary store marker, as <c>AddMartenStore&lt;IInvoicingStore&gt;()</c> registers it.</summary>
public interface IInvoicingStore : Marten.IDocumentStore;

/// <summary>Fisher's ancillary store marker, as <c>AddFisherStore&lt;IFisherInvoicingStore&gt;()</c> registers it.</summary>
public interface IFisherInvoicingStore : Fisher.IDocumentStore;

/// <summary>A host with two event stores, whichever store engine.</summary>
public abstract class TwoStoreHost : StoreHost
{
    /// <summary>The ancillary store's <c>IEventStore.Identity</c> name: its marker's type name, as the store folds it.</summary>
    public abstract string AncillaryName { get; }
}

public sealed class MartenTwoStoreHost : TwoStoreHost
{
    private static int _hosts;
    private readonly string _schema = $"bobcat_cs_two_{Interlocked.Increment(ref _hosts)}";

    public override string AncillaryName => "iinvoicingstore";

    public override bool IsAvailable => PostgresEnvironment.IsCi || PostgresEnvironment.IsAvailable;

    public override string SkipReason => PostgresEnvironment.SkipReason;

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMarten(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema;
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<SharedNote>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddMartenStore<IInvoicingStore>(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema + "_inv";
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Projections.Snapshot<Invoice>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<InvoiceSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<SharedNote>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    protected override async Task BeforeStartAsync()
    {
        await MartenDdl.Gate.WaitAsync();
        await dropSchemasAsync();
    }

    protected override void AfterStartAttempt() => MartenDdl.Gate.Release();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (IsAvailable) await dropSchemasAsync();
    }

    private async Task dropSchemasAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresEnvironment.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE; DROP SCHEMA IF EXISTS {_schema}_inv CASCADE";
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class FisherTwoStoreHost : TwoStoreHost
{
    private readonly string _main = Path.Combine(Path.GetTempPath(), $"bobcat-two-main-{Guid.NewGuid():N}.db");
    private readonly string _invoicing = Path.Combine(Path.GetTempPath(), $"bobcat-two-invoicing-{Guid.NewGuid():N}.db");

    public override string AncillaryName => "ifisherinvoicingstore";

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddFisher(opts =>
            {
                opts.Connection($"Data Source={_main}");

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<SharedNote>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddFisherStore<IFisherInvoicingStore>(opts =>
            {
                opts.Connection($"Data Source={_invoicing}");

                opts.Projections.Snapshot<Invoice>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<InvoiceSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<SharedNote>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var file in new[] { _main, _invoicing }.SelectMany(x => new[] { x, x + "-wal", x + "-shm" }))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temp file; the OS reclaims it
            }
        }
    }
}

/// <summary>One Marten host building its schema at a time, across every host type: Postgres races on its own catalog.</summary>
internal static class MartenDdl
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
