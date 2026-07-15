namespace Wolverine.Persistence;

/// <summary>
/// Identifies a database *server* — as opposed to a logical database. Connections are a
/// server-scoped resource, so any budgeting of them has to be keyed here rather than by
/// <see cref="JasperFx.Descriptors.DatabaseId"/>: in a sharded-tenancy deployment, hundreds of
/// tenant databases can share one server and one connection budget.
/// </summary>
/// <param name="Engine">Matches <see cref="JasperFx.Descriptors.DatabaseDescriptor.Engine"/>, e.g. "PostgreSQL".</param>
/// <param name="ServerName">The host. Matches <see cref="JasperFx.Descriptors.DatabaseDescriptor.ServerName"/>.</param>
/// <param name="Port">
/// Null when the engine folds the port into <paramref name="ServerName"/> — SQL Server's
/// <c>Data Source</c> already carries the port (<c>host,1433</c>) or a named instance
/// (<c>host\SQLEXPRESS</c>), so splitting it back out would only invent ambiguity. PostgreSQL
/// carries the port separately, and it matters: without it, two clusters co-hosted on one box
/// collide onto a single budget.
/// </param>
public readonly record struct DatabaseServerId(string Engine, string ServerName, int? Port)
{
    /// <summary>
    /// The value used as the <c>server</c> metric tag and in diagnostic output. The engine is
    /// deliberately left out — a host:port pair is already unique, and operators recognize it.
    /// </summary>
    public override string ToString()
    {
        return Port.HasValue ? $"{ServerName}:{Port}" : ServerName;
    }
}
