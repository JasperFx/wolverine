namespace Wolverine.Persistence;

/// <summary>
/// Where the <see cref="ConnectionBudgetSnapshot.Max"/> side of a connection budget came from.
/// </summary>
public enum ConnectionBudgetSource
{
    /// <summary>
    /// Declared by the application through <see cref="ConnectionBudgets.ForServer(string,int,int)"/>. This is the
    /// authoritative source: behind a pooler (pgBouncer et al.) the server's own
    /// <c>max_connections</c> says nothing about how many connections the *application* may take.
    /// </summary>
    Configured,

    /// <summary>
    /// Read off the server itself (<c>max_connections</c> / <c>@@MAX_CONNECTIONS</c>) because
    /// nothing was configured for this server. A fallback, and labelled as one so operators can
    /// tell a declared budget from a guessed one.
    /// </summary>
    Probed,

    /// <summary>
    /// No budget could be established — nothing configured, and the probe for the server's own
    /// limit failed or was denied (SQL Server's <c>VIEW SERVER STATE</c> is a common casualty of
    /// locked-down hosting). <see cref="ConnectionBudgetSnapshot.Used"/> is still meaningful;
    /// <see cref="ConnectionBudgetSnapshot.Max"/> is null and no utilization can be computed.
    /// </summary>
    Unknown
}

/// <summary>
/// A single observation of one database server's connection budget, published once per server per
/// metrics sweep. See wolverine#3397.
/// </summary>
/// <param name="Server">The server this budget belongs to.</param>
/// <param name="Used">
/// Connections currently open on the server, across *all* applications — this is a server-scoped
/// resource, so the number deliberately is not Wolverine-only. Behind a transaction-pooling
/// pgBouncer it counts pooler-to-server backends, not client sessions.
/// </param>
/// <param name="Max">The budget, or null when <paramref name="Source"/> is <see cref="ConnectionBudgetSource.Unknown"/>.</param>
/// <param name="Source">Where <paramref name="Max"/> came from.</param>
public record ConnectionBudgetSnapshot(
    DatabaseServerId Server,
    int Used,
    int? Max,
    ConnectionBudgetSource Source)
{
    /// <summary>
    /// Used/Max as a fraction, or null when there is no known budget to measure against. The
    /// hysteresis thresholds of the (not yet shipped) adaptive back-off phase key off this.
    /// </summary>
    public double? Utilization => Max is > 0 ? Used / (double)Max.Value : null;
}
