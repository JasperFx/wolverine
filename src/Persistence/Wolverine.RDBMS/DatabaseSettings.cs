using System.Data.Common;

namespace Wolverine.RDBMS;

public class DatabaseSettings
{
    public DbDataSource? DataSource { get; set; }

    public string? ConnectionString { get; set; }
    public string? SchemaName { get; set; }

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