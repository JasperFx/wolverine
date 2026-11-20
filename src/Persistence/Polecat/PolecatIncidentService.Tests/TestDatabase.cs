using System.Runtime.CompilerServices;
using IntegrationTests;

namespace PolecatIncidentService.Tests;

internal static class TestDatabase
{
    /// <summary>
    /// GH-5635: the sample's own default connection string names a docker-compose database that the 7.x (main)
    /// suites use as well. Point the sample at the 6.x test database instead, unless the environment already
    /// names one.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTheTestDatabase()
    {
        const string variable = "ConnectionStrings__SqlServer";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Environment.SetEnvironmentVariable(variable, Servers.SqlServerConnectionString);
        }
    }
}
