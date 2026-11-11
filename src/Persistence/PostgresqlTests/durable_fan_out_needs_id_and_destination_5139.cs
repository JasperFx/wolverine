using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-5139. With one handler chain per handler type, two handler types of one message type bound to two durable
/// listeners of an external transport receive the same envelope id whenever the broker fans one message out to
/// both queues. IdAndDestination, the 7.0 default, tells the copies apart. Under an explicit MessageIdentity.IdOnly
/// the durable inbox would reject the second copy as a duplicate, so Wolverine refuses to start.
/// </summary>
public class durable_fan_out_needs_id_and_destination_5139
{
    private static Task<IHost> startHost(MessageIdentity? identity, bool restoreV6Defaults = false)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (restoreV6Defaults) opts.RestoreV6Defaults();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FanOutShippingHandler>()
                    .IncludeType<FanOutBillingHandler>();

                // A schema per identity: the two identities give the inbox different primary keys
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    identity == MessageIdentity.IdOnly || restoreV6Defaults ? "fan_out_id_only" : "fan_out_id_and_destination");

                if (identity.HasValue)
                {
                    opts.Durability.MessageIdentity = identity.Value;
                }

                opts.ListenAtPort(PortFinder.GetAvailablePort()).UseDurableInbox()
                    .AddStickyHandler(typeof(FanOutShippingHandler));
                opts.ListenAtPort(PortFinder.GetAvailablePort()).UseDurableInbox()
                    .AddStickyHandler(typeof(FanOutBillingHandler));
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task startup_fails_under_IdOnly(bool throughRestoreV6Defaults)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = throughRestoreV6Defaults
                ? await startHost(null, restoreV6Defaults: true)
                : await startHost(MessageIdentity.IdOnly);
        });

        ex.Message.ShouldContain(typeof(FanOutOrderPlaced).FullNameInCode());
        ex.Message.ShouldContain("MessageIdentity.IdAndDestination");
    }

    [Fact]
    public async Task starts_under_the_7_0_default()
    {
        using var host = await startHost(null);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task starts_with_an_explicit_IdAndDestination()
    {
        using var host = await startHost(MessageIdentity.IdAndDestination);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record FanOutOrderPlaced(Guid Id);

[WolverineIgnore]
public class FanOutShippingHandler
{
    public void Handle(FanOutOrderPlaced message)
    {
    }
}

[WolverineIgnore]
public class FanOutBillingHandler
{
    public void Handle(FanOutOrderPlaced message)
    {
    }
}

/// <summary>
/// GH-5139. A Wolverine 6.x inbox is keyed by envelope id alone. Starting Wolverine 7 against it, with the new
/// IdAndDestination default, migrates the incoming envelope table's primary key to (id, received_at)
/// </summary>
public class upgrading_an_id_only_inbox_to_id_and_destination_5139 : IAsyncLifetime
{
    private const string SchemaName = "identity_upgrade_5139";

    public async ValueTask InitializeAsync()
    {
        await dropSchemaAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await dropSchemaAsync();
    }

    private static async Task dropSchemaAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"drop schema if exists {SchemaName} cascade";
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<IHost> startHost(bool restoreV6Defaults)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (restoreV6Defaults) opts.RestoreV6Defaults();

                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> incomingPrimaryKeyColumnsAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          select a.attname
                          from pg_index i
                          join pg_class c on c.oid = i.indrelid
                          join pg_namespace n on n.oid = c.relnamespace
                          join pg_attribute a on a.attrelid = c.oid and a.attnum = any(i.indkey)
                          where i.indisprimary and n.nspname = @schema and c.relname = 'wolverine_incoming_envelopes'
                          order by a.attname
                          """;
        cmd.Parameters.AddWithValue("schema", SchemaName);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    [Fact]
    public async Task the_inbox_primary_key_gains_the_destination()
    {
        using (var v6 = await startHost(restoreV6Defaults: true))
        {
            await v6.StopAsync(TestContext.Current.CancellationToken);
        }

        (await incomingPrimaryKeyColumnsAsync()).ShouldBe(["id"]);

        using (var v7 = await startHost(restoreV6Defaults: false))
        {
            await v7.StopAsync(TestContext.Current.CancellationToken);
        }

        (await incomingPrimaryKeyColumnsAsync()).ShouldBe(["id", "received_at"]);
    }
}
