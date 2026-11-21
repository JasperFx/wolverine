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

// GH-5725: Describe() reads Multiplexing and WriteCoalescingBufferThresholdBytes through the connection
// string dictionary because Npgsql 11 removed both properties. On the pinned driver the properties still
// exist, so the hand-built entries must stay identical to what OptionsValue.Read reports for them.
public class describe_reports_legacy_npgsql_options : PostgresqlContext
{
    [Theory]
    [InlineData("")]
    [InlineData("Multiplexing=true;Write Coalescing Buffer Threshold Bytes=4096")]
    public void legacy_options_match_the_property_reads(string options)
    {
        var connectionString = Servers.PostgresConnectionString + ";" + options;
        var store = new PostgresqlMessageStore(new DatabaseSettings { ConnectionString = connectionString },
            new DurabilitySettings(), NpgsqlDataSource.Create(connectionString),
            NullLogger<PostgresqlMessageStore>.Instance);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        var properties = store.Describe().Properties;

        shouldMatch(properties, OptionsValue.Read(builder, x => x.Multiplexing));
        shouldMatch(properties, OptionsValue.Read(builder, x => x.WriteCoalescingBufferThresholdBytes));
    }

    private static void shouldMatch(IEnumerable<OptionsValue> properties, OptionsValue expected)
    {
        var actual = properties.Where(x => x.Name == expected.Name).ShouldHaveSingleItem();

        actual.Subject.ShouldBe(expected.Subject);
        actual.Type.ShouldBe(expected.Type);
        actual.Value.ShouldBe(expected.Value);
        actual.RawValue.ShouldBe(expected.RawValue);
    }
}
