using IntegrationTests;

namespace SqlServerTests;

/// <summary>
/// Lane-scoped names for the sibling databases some suites create beside the main catalog
/// (multi-tenancy's db1/db2/db3, the NServiceBus dedicated-database tests). Under parallelized CI
/// each worker lane runs against its own catalog (wolverine_w0..N-1 via WOLVERINE_SQLSERVER), and
/// two lanes creating the literal db1 would collide on the shared server exactly the way shared
/// schemas do — so a sibling's name is derived from the lane's catalog. Against the default master
/// catalog the names stay exactly what they always were.
/// </summary>
public static class LaneDatabases
{
    public static string Name(string baseName)
    {
        var catalog = Servers.SqlServerDatabaseName;
        return string.Equals(catalog, "master", StringComparison.OrdinalIgnoreCase)
            ? baseName
            : $"{catalog}_{baseName}";
    }
}
