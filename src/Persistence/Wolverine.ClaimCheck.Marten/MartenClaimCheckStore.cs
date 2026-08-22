using JasperFx.Core.Reflection;
using Marten;
using Marten.Storage;
using Npgsql;
using Weasel.Postgresql;
using Wolverine.ClaimCheck.Postgresql;

namespace Wolverine.ClaimCheck.Marten;

/// <summary>
/// A claim-check store that lives inside the application's own Marten database. Off-loaded payloads are
/// written as <c>bytea</c> rows in a Wolverine-managed table in Marten's schema — no S3 / Azure / GCS
/// account, and no second database. See GH-3566.
/// </summary>
/// <remarks>
/// Payloads are stored as raw <c>bytea</c> rather than as a Marten document on purpose. A Marten document
/// is JSONB, which base64-encodes a binary body for roughly 33% storage overhead plus encode/decode cost
/// on every payload — precisely the wrong trade for the large-payload workload claim checks exist to
/// serve. What this backend takes from Marten is the <i>connectivity and schema</i>, which is what
/// "zero new infrastructure" actually means here.
///
/// Because the payload table is not a Marten document, it does not participate in Marten's schema
/// management: the table is created lazily on first use, exactly like the standalone PostgreSQL backend.
/// </remarks>
public class MartenClaimCheckStore : PostgresqlClaimCheckStore
{
    /// <summary>
    /// Create a claim-check store against the PostgreSQL database behind <paramref name="store"/>.
    /// </summary>
    /// <param name="store">The application's Marten document store.</param>
    /// <param name="schemaName">
    /// Schema that owns the claim check table. Defaults to Marten's own <c>DatabaseSchemaName</c>, so the
    /// payload table sits alongside the documents it was configured next to.
    /// </param>
    /// <param name="tableName">Name of the claim check table.</param>
    public MartenClaimCheckStore(IDocumentStore store, string? schemaName = null,
        string tableName = DefaultTableName)
        : base(DataSourceFor(store), schemaName ?? SchemaNameFor(store), tableName)
    {
    }

    /// <summary>
    /// Resolve the <see cref="NpgsqlDataSource"/> behind a Marten store. For a store using
    /// conjoined/separate-database tenancy this is the <i>master</i> database — claim-check payloads are
    /// keyed by an opaque token and carry no tenant semantics, so they live in one place rather than being
    /// scattered across tenant databases where the receiving node might not know which one to read.
    /// </summary>
    public static NpgsqlDataSource DataSourceFor(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var documentStore = store as DocumentStore
                            ?? throw new ArgumentException(
                                $"Expected a Marten DocumentStore, but got {store.GetType().FullName}.",
                                nameof(store));

        if (documentStore.Tenancy is ITenancyWithMasterDatabase master)
        {
            return master.TenantDatabase.DataSource;
        }

        return documentStore.Storage.Database.As<PostgresqlDatabase>().DataSource;
    }

    /// <summary>Marten's configured database schema name, used as the default for the payload table.</summary>
    public static string SchemaNameFor(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Options.DatabaseSchemaName;
    }
}
