using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     Source of known tenant databases
/// </summary>
public interface IMessageDatabaseSource : ITenantedMessageStore
{
    /// <summary>
    /// Add extra configuration to every actively used tenant database
    /// </summary>
    /// <param name="configureDatabase"></param>
    public ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase);
}