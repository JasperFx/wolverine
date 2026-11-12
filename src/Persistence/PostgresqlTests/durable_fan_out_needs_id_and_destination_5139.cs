using IntegrationTests;
using System.Collections.Concurrent;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// both queues. The IdAndDestination opt-in tells the copies apart. Under MessageIdentity.IdOnly, the default,
/// the durable inbox would reject the second copy as a duplicate, so Wolverine warns at startup. Only a warning:
/// whether the broker really copies one message into both queues is not something Wolverine can see.
/// </summary>
public class durable_fan_out_needs_id_and_destination_5139
{
    private readonly WarningCapture theWarnings = new();

    private Task<IHost> startHost(MessageIdentity? identity)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(theWarnings))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FanOutShippingHandler>()
                    .IncludeType<FanOutBillingHandler>();

                // A schema per identity: the two identities give the inbox different primary keys
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    identity == MessageIdentity.IdAndDestination ? "fan_out_id_and_destination" : "fan_out_id_only");

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
    public async Task warns_at_startup_under_IdOnly(bool explicitly)
    {
        using var host = await startHost(explicitly ? MessageIdentity.IdOnly : null);

        var warning = theWarnings.Messages.ShouldHaveSingleItem();
        warning.ShouldContain(typeof(FanOutOrderPlaced).FullNameInCode());
        warning.ShouldContain("MessageIdentity.IdAndDestination");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task no_warning_with_an_explicit_IdAndDestination()
    {
        using var host = await startHost(MessageIdentity.IdAndDestination);
        theWarnings.Messages.ShouldBeEmpty();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Only the GH-5139 warning, which is the one naming the durable listening endpoints
    private sealed class WarningCapture : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;

            var message = formatter(state, exception);
            if (message.Contains("durable listening endpoint of an external transport")) _messages.Enqueue(message);
        }
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
/// GH-5139. An inbox under the default IdOnly identity is keyed by envelope id alone. Starting against it with the
/// IdAndDestination opt-in migrates the incoming envelope table's primary key to (id, received_at)
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

    private static Task<IHost> startHost(MessageIdentity? identity)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (identity.HasValue) opts.Durability.MessageIdentity = identity.Value;

                // GH-5519: the partitioned inbox (the 7.0 default) adds status to the primary key; this is about identity
                opts.Durability.EnableInboxPartitioning = false;

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
    public async Task the_inbox_primary_key_gains_the_destination_with_the_opt_in()
    {
        using (var idOnly = await startHost(null))
        {
            await idOnly.StopAsync(TestContext.Current.CancellationToken);
        }

        (await incomingPrimaryKeyColumnsAsync()).ShouldBe(["id"]);

        using (var idAndDestination = await startHost(MessageIdentity.IdAndDestination))
        {
            await idAndDestination.StopAsync(TestContext.Current.CancellationToken);
        }

        (await incomingPrimaryKeyColumnsAsync()).ShouldBe(["id", "received_at"]);
    }
}
