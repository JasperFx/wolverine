using Npgsql;
using Xunit;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// Where the Marten integration tests find their database, and whether one is actually there.
/// </summary>
public static class PostgresEnvironment
{
    private static readonly Lazy<bool> _available = new(probe, isThreadSafe: true);

    /// <summary>
    /// This repository's docker-compose Postgres (5433), or whatever <c>ConnectionStrings__postgres</c> /
    /// <c>WOLVERINE_POSTGRES</c> name: <see cref="IntegrationTests.Servers"/> is the one place that knows.
    /// </summary>
    public static string ConnectionString => IntegrationTests.Servers.PostgresConnectionString;

    /// <summary>GitHub Actions (and most CI systems) set CI=true.</summary>
    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable => _available.Value;

    public static string SkipReason =>
        $"No Postgres reachable at {describe()}. Run `docker compose up -d postgresql` from the repo root, " +
        "or point ConnectionStrings__postgres (or WOLVERINE_POSTGRES) at your own database. (This skip never applies on CI.)";

    private static bool probe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Timeout = 3 };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Host/database only — never echo the password into test output.</summary>
    private static string describe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch
        {
            return "the configured connection string";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need a real Postgres.
///
/// <para>Off CI, a missing database skips the test with an actionable message so a fresh clone
/// still runs green without Docker. On CI the skip is deliberately NOT applied — the workflow
/// provides a Postgres service, and a missing database there has to fail the build rather than
/// quietly report a pass.</para>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!PostgresEnvironment.IsCi && !PostgresEnvironment.IsAvailable)
        {
            Skip = PostgresEnvironment.SkipReason;
        }
    }
}
