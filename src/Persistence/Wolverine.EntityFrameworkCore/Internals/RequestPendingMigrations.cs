namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// Sent from a monitoring tool (CritterWatch #102) to a service, asking it
/// to evaluate <c>IMigrator.GetPendingMigrationsAsync</c> for the
/// <see cref="DbContextUsage"/> identified by <paramref name="SubjectUri"/>
/// and report back via <see cref="PendingMigrationsReported"/>.
/// </summary>
/// <remarks>
/// On-demand rather than baked into the regular capabilities snapshot because
/// EF Core's pending-migrations probe opens a connection to the target
/// database and reads the <c>__EFMigrationsHistory</c> table — that's a
/// synchronous round-trip per DbContext, and operators don't want to pay it
/// on every capabilities refresh. Triggered from the Storage tab's "Check
/// pending migrations" action button.
/// </remarks>
/// <param name="SubjectUri">
/// The <c>efcore://</c> URI of the <c>IDbContextUsageSource</c> to evaluate
/// — same identity that ships back on <c>DbContextUsage.SubjectUri</c>.
/// </param>
public record RequestPendingMigrations(Uri SubjectUri);

/// <summary>
/// Response to <see cref="RequestPendingMigrations"/> — the names of the
/// migrations that have been generated but not yet applied to the target
/// database, the timestamp at which the probe ran, and an explanatory
/// error string when the probe failed (transient connection issue,
/// insufficient permissions, or provider that doesn't support migrations).
/// </summary>
/// <param name="SubjectUri">
/// Echoes the <see cref="RequestPendingMigrations.SubjectUri"/> so the UI
/// can match the response back to the request that triggered it.
/// </param>
/// <param name="MigrationNames">
/// Names of pending migrations in the order EF Core would apply them.
/// Empty when the database is up to date or the probe failed; check
/// <paramref name="Error"/> to disambiguate.
/// </param>
/// <param name="CheckedAt">
/// UTC timestamp at which the probe completed — operators monitoring
/// fast-changing migration sets need this to know how stale the answer is.
/// </param>
/// <param name="Error">
/// Non-null when the probe failed for any reason (connection failure,
/// missing permission, unsupported provider). Operators see this string
/// in the Storage tab as the explanatory text under "Pending migrations".
/// </param>
public record PendingMigrationsReported(
    Uri SubjectUri,
    string[] MigrationNames,
    DateTimeOffset CheckedAt,
    string? Error);
