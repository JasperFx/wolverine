using System.Data.Common;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Core;
using Wolverine.Persistence.Durability;

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
    public MessageStoreRole Role { get; set; } = MessageStoreRole.Ancillary;
    
    /// <summary>
    /// If the main database, add a tenant lookup table
    /// </summary>
    public bool AddTenantLookupTable { get; set; } = false;

    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    public int ScheduledJobLockId { get; set; } = 20000;
    
    /// <summary>
    /// Default databases by tenant and connection string to use for seeding
    /// "master table tenancy"
    /// </summary>
    public ITenantedSource<string>? TenantConnections { get; set; }
}
