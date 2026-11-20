using IntegrationTests;

namespace SqlServerTests;

/// <summary>
/// Lane-scoped names for the sibling databases some suites create beside the main catalog
/// (multi-tenancy's db1/db2/db3, the NServiceBus dedicated-database tests). Under parallelized CI
/// each worker lane runs against its own catalog (wolverine6_w0..N-1 via ConnectionStrings__sqlserver), and
/// two lanes creating the literal db1 would collide on the shared server exactly the way shared
/// schemas do — so a sibling's name is derived from the lane's catalog. The default catalog and the
/// lanes all carry the 6.x <see cref="Servers.DatabasePrefix"/>, so the siblings do too (GH-5635); a
/// connection string pointed at master gets the prefix on its own.
/// </summary>
public static class LaneDatabases
{
    public static string Name(string baseName)
    {
        var catalog = Servers.SqlServerDatabaseName;
        return string.Equals(catalog, "master", StringComparison.OrdinalIgnoreCase)
            ? Servers.DatabaseName(baseName)
            : $"{catalog}_{baseName}";
    }
}
