using JasperFx.Descriptors;

namespace Wolverine.Persistence;

/// <summary>
/// Declares the connection budget for each database server this application talks to, and decides
/// whether the budget machinery runs at all. Reached through
/// <see cref="DurabilitySettings.ConnectionBudgets"/>. See wolverine#3397.
/// </summary>
/// <remarks>
/// The <c>max</c> side of the budget is configuration rather than a probed <c>max_connections</c>
/// on purpose. Any pooler in front of the database — pgBouncer being the common one — makes the
/// server's own limit a poor description of what the application is allowed to take, so the budget
/// is declared and the probe supplies only the <c>used</c> side.
/// </remarks>
public class ConnectionBudgets
{
    private readonly List<Registration> _registrations = new();

    private sealed record Registration(string ServerName, int? Port, int MaxConnections);

    /// <summary>
    /// Force the connection-budget probing on or off. Leave null (the default) to let Wolverine
    /// decide: probing is on when the message stores span statically-known multiple databases
    /// (Marten's <c>MultiTenantedWithShardedDatabases</c> — the many-databases-per-server shape
    /// that per-server budgeting exists to serve), or when any budget has been declared below.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Declare the connection budget for a server that carries its port separately — PostgreSQL.
    /// The port is part of the key, so two clusters co-hosted on one box get their own budgets.
    /// </summary>
    /// <param name="serverName">The host, as it appears in the connection string.</param>
    /// <param name="port">The port, as it appears in the connection string.</param>
    /// <param name="maxConnections">How many connections this application may take against the server.</param>
    public ConnectionBudgets ForServer(string serverName, int port, int maxConnections)
    {
        return register(serverName, port, maxConnections);
    }

    /// <summary>
    /// Declare the connection budget for a server addressed by name alone. Use this for SQL Server,
    /// whose <c>Data Source</c> already carries the port or named instance
    /// (<c>host,1433</c> / <c>host\SQLEXPRESS</c>) — pass that string verbatim.
    /// </summary>
    /// <remarks>
    /// For PostgreSQL this registers a *host-wide* budget covering every port on the host. An exact
    /// host+port registration always wins over it.
    /// </remarks>
    public ConnectionBudgets ForServer(string serverName, int maxConnections)
    {
        return register(serverName, null, maxConnections);
    }

    private ConnectionBudgets register(string serverName, int? port, int maxConnections)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("The server name is required", nameof(serverName));
        }

        if (maxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections), maxConnections,
                "The connection budget must be greater than zero.");
        }

        // Last registration wins, so re-declaring a server (say, from a later configuration
        // callback) overrides rather than silently ending up with two budgets for one key.
        _registrations.RemoveAll(x =>
            string.Equals(x.ServerName, serverName, StringComparison.OrdinalIgnoreCase) && x.Port == port);

        _registrations.Add(new Registration(serverName, port, maxConnections));

        return this;
    }

    /// <summary>
    /// Any budget declared at all?
    /// </summary>
    public bool HasAny => _registrations.Count > 0;

    /// <summary>
    /// The declared budget for a server, or null when nothing covers it. An exact host+port match
    /// wins over a host-wide registration.
    /// </summary>
    public int? MaxFor(DatabaseServerId server)
    {
        var exact = _registrations.FirstOrDefault(x =>
            string.Equals(x.ServerName, server.ServerName, StringComparison.OrdinalIgnoreCase)
            && x.Port == server.Port);

        if (exact is not null)
        {
            return exact.MaxConnections;
        }

        var hostWide = _registrations.FirstOrDefault(x =>
            string.Equals(x.ServerName, server.ServerName, StringComparison.OrdinalIgnoreCase)
            && x.Port is null);

        return hostWide?.MaxConnections;
    }

    /// <summary>
    /// Should the metrics sweeper probe connection budgets in this application?
    /// </summary>
    internal bool IsActive(DatabaseCardinality cardinality)
    {
        if (Enabled.HasValue)
        {
            return Enabled.Value;
        }

        // Declaring a budget is itself an opt-in — an operator who names a server's limit wants to
        // see it tracked, whatever the tenancy shape.
        if (HasAny)
        {
            return true;
        }

        // Otherwise only the many-databases-on-one-server shape, where a per-server budget tells
        // you something a per-database count cannot. A single database has nothing to aggregate.
        return cardinality == DatabaseCardinality.StaticMultiple;
    }
}
