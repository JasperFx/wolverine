using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Http.Transport.Push;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Util;

namespace Wolverine.Http.Tests.Transport;

/// <summary>
///     GH-5049 with a database per tenant. The push receiver's inbox claim, and the check that tells a claim apart from
///     a handled message (IMessageInboxHandledCheck), go through MultiTenantedMessageStore, which routes them by the
///     envelope's tenant id. The claim and the handled row have to land in, and be found in, that tenant's database
/// </summary>
public class push_receiver_inbox_claim_tenanted_5049 : IAsyncLifetime
{
    private const string Route = "/_wolverine/push/tenanted-claims";
    private const string Schema = "push_claim_tenanted";
    private const string TenantHeader = "x-tenant";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string theRedConnectionString = null!;
    private string theBlueConnectionString = null!;

    public async ValueTask InitializeAsync()
    {
        theRedConnectionString = await databaseAsync("push_claim_red");
        theBlueConnectionString = await databaseAsync("push_claim_blue");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> databaseAsync(string name)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);

            await using var exists = conn.CreateCommand();
            exists.CommandText = "select count(*) from pg_database where datname = @name";
            exists.Parameters.AddWithValue("name", name);

            if (Convert.ToInt64(await exists.ExecuteScalarAsync(Token)) == 0)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = $"create database {name}";
                await create.ExecuteNonQueryAsync(Token);
            }
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = name }.ConnectionString;
    }

    private async Task<IAlbaHost> hostWith(DurabilityMode mode)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = mode;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ClaimedPushHandler));
            opts.Services.AddSingleton<ClaimedPushGate>();

            opts.OnException<ClaimedPushRequeueException>().Requeue();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema)
                .RegisterStaticTenants(tenants =>
                {
                    tenants.Register("red", theRedConnectionString);
                    tenants.Register("blue", theBlueConnectionString);
                });

            opts.Services.AddResourceSetupOnStartup();
        });

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverinePushReceiver(Route, new TenantedClaimedPushMapper(), o =>
            {
                o.AllowAnonymous();
                o.DeduplicateWithInbox = true;
            });
        });

        await host.ResetResourceState(Token);
        return host;
    }

    private static async Task<HttpStatusCode> push(HttpClient client, string tenant, Guid id, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ClaimedPush(name) { Id = id }), Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(TenantHeader, tenant);

        var response = await client.SendAsync(request, Token);
        return response.StatusCode;
    }

    // The status of the message's inbox row in one database, or null when that database has no row for it
    private static async Task<string?> inboxStatusAsync(string connectionString, Guid id)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select {DatabaseConstants.Status} from {Schema}.{DatabaseConstants.IncomingTable} where id = @id";
        cmd.Parameters.AddWithValue("id", id);

        return (string?)await cmd.ExecuteScalarAsync(Token);
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.ServerlessWithOutbox)]
    public async Task a_concurrent_redelivery_is_refused_from_the_claim_in_the_tenants_database(DurabilityMode mode)
    {
        await using var host = await hostWith(mode);
        var client = host.Server.CreateClient();
        var gate = host.Services.GetRequiredService<ClaimedPushGate>();

        var id = Guid.NewGuid();
        var first = push(client, "red", id, "slow");
        await gate.Started.Task.WaitAsync(10.Seconds(), Token);

        // The claim is in red's database, and only there
        (await inboxStatusAsync(theRedConnectionString, id)).ShouldBe(EnvelopeStatus.Incoming.ToString());
        (await inboxStatusAsync(theBlueConnectionString, id)).ShouldBeNull();
        (await inboxStatusAsync(Servers.PostgresConnectionString, id)).ShouldBeNull();

        // The ack deadline passed and the broker delivered it again
        (await push(client, "red", id, "slow")).ShouldBe(HttpStatusCode.InternalServerError);
        gate.Runs.ShouldBe(1);

        gate.Release.SetResult();
        (await first).ShouldBe(HttpStatusCode.OK);

        (await inboxStatusAsync(theRedConnectionString, id)).ShouldBe(EnvelopeStatus.Handled.ToString());

        // A later redelivery is found handled in red's database and acked without running it
        (await push(client, "red", id, "slow")).ShouldBe(HttpStatusCode.OK);
        gate.Runs.ShouldBe(1);
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.ServerlessWithOutbox)]
    public async Task a_retry_releases_the_claim_in_the_tenants_database(DurabilityMode mode)
    {
        await using var host = await hostWith(mode);
        var client = host.Server.CreateClient();
        var gate = host.Services.GetRequiredService<ClaimedPushGate>();

        var id = Guid.NewGuid();
        (await push(client, "blue", id, "requeue")).ShouldBe(HttpStatusCode.InternalServerError);

        // Released, so the broker's redelivery can claim it and run it again
        (await inboxStatusAsync(theBlueConnectionString, id)).ShouldBeNull();
        (await push(client, "blue", id, "requeue")).ShouldBe(HttpStatusCode.InternalServerError);
        gate.Runs.ShouldBe(2);
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.ServerlessWithOutbox)]
    public async Task a_message_handled_for_one_tenant_is_recorded_in_that_tenants_database_only(DurabilityMode mode)
    {
        await using var host = await hostWith(mode);
        var client = host.Server.CreateClient();

        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        (await push(client, "red", red, "fast")).ShouldBe(HttpStatusCode.OK);
        (await push(client, "blue", blue, "fast")).ShouldBe(HttpStatusCode.OK);

        (await inboxStatusAsync(theRedConnectionString, red)).ShouldBe(EnvelopeStatus.Handled.ToString());
        (await inboxStatusAsync(theBlueConnectionString, red)).ShouldBeNull();

        (await inboxStatusAsync(theBlueConnectionString, blue)).ShouldBe(EnvelopeStatus.Handled.ToString());
        (await inboxStatusAsync(theRedConnectionString, blue)).ShouldBeNull();

        (await inboxStatusAsync(Servers.PostgresConnectionString, red)).ShouldBeNull();
        (await inboxStatusAsync(Servers.PostgresConnectionString, blue)).ShouldBeNull();
    }
}

/// <summary>
///     Like <see cref="ClaimedPushMapper" />, with the tenant id taken from a request header
/// </summary>
public class TenantedClaimedPushMapper : IPushEnvelopeMapper
{
    public async ValueTask<IReadOnlyList<Envelope>> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        using var stream = new MemoryStream();
        await context.Request.Body.CopyToAsync(stream, cancellation);
        var data = stream.ToArray();

        var message = JsonSerializer.Deserialize<ClaimedPush>(data)!;

        return
        [
            new Envelope
            {
                Id = message.Id,
                TenantId = context.Request.Headers["x-tenant"].ToString(),
                Data = data,
                MessageType = typeof(ClaimedPush).ToMessageTypeName(),
                ContentType = "application/json"
            }
        ];
    }
}
