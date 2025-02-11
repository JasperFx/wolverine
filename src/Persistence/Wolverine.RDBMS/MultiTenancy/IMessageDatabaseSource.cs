namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     Source of known tenant databases
/// </summary>
[Obsolete("Replace with IMessageStoreSource")]
public interface IMessageDatabaseSource
{
    ValueTask<IMessageDatabase> FindDatabaseAsync(string tenantId);
    Task RefreshAsync();

    IReadOnlyList<IMessageDatabase> AllActive();

    /// <summary>
    /// Add extra configuration to every actively used tenant database
    /// </summary>
    /// <param name="configureDatabase"></param>
    public ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase);
}