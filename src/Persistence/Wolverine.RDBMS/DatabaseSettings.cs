using System.Data.Common;
using JasperFx;
using Wolverine.Persistence.MultiTenancy;

namespace Wolverine.RDBMS;

public class DatabaseSettings
{
    public DbDataSource? DataSource { get; set; }

    public string? ConnectionString { get; set; }
    public string? SchemaName { get; set; }
    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

    /// <summary>
    ///     Is this database the master database for node storage and any kind of command queueing?
    /// </summary>
    public bool IsMaster { get; set; }

    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    public int ScheduledJobLockId { get; set; } = 20000;
}

// Just use a separate setting for doing this by DbDataSource
public class MultiTenancySettings
{
    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    public int ScheduledJobLockId { get; set; } = 20000;
    
    // TODO -- use the default from JasperFxOptions
    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

    public string? SchemaName { get; set; } = "wolverine";

    /// <summary>
    /// The "master" database that will store node information and be the default
    /// Wolverine envelope storage when there is not tenant specified or in non-tenanted
    /// operations. This *can* be one of the specific tenant connection strings. If not specified,
    /// Wolverine will look for the connection string for the *Default* tenant id
    /// </summary>
    public string? MasterConnectionString { get; set; }

    public StaticConnectionStringSource ConnectionStrings { get; } = new();
}