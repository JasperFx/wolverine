using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests;

public class a_cascade_that_cannot_be_stored_fails_its_handler : PostgresqlContext, IAsyncLifetime
{
    private const string Schema = "unstorable_cascade";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(Schema);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(UnstorableCascadeSourceHandler))
                    .IncludeType(typeof(UnstorableCascadeHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = Schema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await executeAsync($"""
            create function {Schema}.refuse_envelope() returns trigger language plpgsql as $$
            begin
                raise exception 'the message store refuses this envelope';
            end $$;
            create trigger refuse_unstorable_cascade before insert on {Schema}.wolverine_incoming_envelopes
                for each row when (new.message_type = '{typeof(UnstorableCascade).ToMessageTypeName()}')
                execute function {Schema}.refuse_envelope();
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_source_message_is_dead_lettered_instead_of_marked_handled()
    {
        var source = new UnstorableCascadeSource(Guid.NewGuid());

        await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(source));

        var sourceType = typeof(UnstorableCascadeSource).ToMessageTypeName();

        (await countAsync($"select count(*) from {Schema}.wolverine_incoming_envelopes where message_type = '{sourceType}' and status = 'Handled'"))
            .ShouldBe(0);
        (await countAsync($"select count(*) from {Schema}.wolverine_dead_letters where message_type = '{sourceType}'"))
            .ShouldBe(1);
        UnstorableCascadeHandler.Handled.ShouldBe(0);
    }

    private static async Task executeAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        return (long)(await new NpgsqlCommand(sql, conn).ExecuteScalarAsync())!;
    }
}

public record UnstorableCascadeSource(Guid Id);

public record UnstorableCascade(Guid Id);

public static class UnstorableCascadeSourceHandler
{
    public static UnstorableCascade Handle(UnstorableCascadeSource message) => new(message.Id);
}

public static class UnstorableCascadeHandler
{
    public static int Handled;

    public static void Handle(UnstorableCascade message) => Interlocked.Increment(ref Handled);
}
