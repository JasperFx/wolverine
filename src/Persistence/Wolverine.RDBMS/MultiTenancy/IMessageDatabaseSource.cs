namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     Source of known tenant databases
/// </summary>
public interface IMessageDatabaseSource
{
    ValueTask<IMessageDatabase> FindDatabaseAsync(string tenantId);
    Task InitializeAsync();

    IReadOnlyList<IMessageDatabase> AllActive();
}