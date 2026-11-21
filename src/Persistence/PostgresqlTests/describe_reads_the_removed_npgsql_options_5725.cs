using IntegrationTests;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-5725. Npgsql 11 removed the Multiplexing and WriteCoalescingBufferThresholdBytes properties from
/// NpgsqlConnectionStringBuilder, and Describe() read both, so on Npgsql 11 the method could not even be compiled and
/// the host died at startup. Describe() now reads them through the connection string's keywords. This test only uses
/// keywords too, so it runs the same on a driver that still has the options and on one that no longer does
/// </summary>
public class describe_reads_the_removed_npgsql_options_5725 : PostgresqlContext
{
    private static readonly bool DriverStillHasMultiplexing = new NpgsqlConnectionStringBuilder().ContainsKey("Multiplexing");

    [Fact]
    public void describe_reports_the_options_where_the_driver_still_has_them()
    {
        var connectionString = DriverStillHasMultiplexing
            ? Servers.PostgresConnectionString + ";Multiplexing=true;Write Coalescing Buffer Threshold Bytes=4096"
            : Servers.PostgresConnectionString;

        using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgresqlMessageStore(new DatabaseSettings { ConnectionString = connectionString },
            new DurabilitySettings(), dataSource, NullLogger<PostgresqlMessageStore>.Instance, ownsDataSource: false);

        // Describe() itself is the regression: it used to fail outright on Npgsql 11
        var properties = store.Describe().Properties;

        var multiplexing = properties.Where(x => x.Name == "Multiplexing").ToArray();
        var threshold = properties.Where(x => x.Name == "WriteCoalescingBufferThresholdBytes").ToArray();

        if (DriverStillHasMultiplexing)
        {
            multiplexing.ShouldHaveSingleItem().Value.ShouldBe("True");
            threshold.ShouldHaveSingleItem().Value.ShouldBe("4096");
        }
        else
        {
            multiplexing.ShouldBeEmpty();
            threshold.ShouldBeEmpty();
        }
    }
}
