using System.Net.Http.Json;
using Npgsql;

namespace DurabilityChaosRig;

/// <summary>
/// CHAOS_TOXIPROXY=1 puts a Toxiproxy proxy between the nodes and PostgreSQL, so the driver can inject latency,
/// connection resets and black holes into the database traffic with the "net" fault. The driver itself always
/// connects directly. Needs a running Toxiproxy container, see README.md
/// </summary>
public static class Toxiproxy
{
    private const string ProxyName = "chaos-pg";
    private const int ListenPort = 25433;

    public static bool Enabled => Environment.GetEnvironmentVariable("CHAOS_TOXIPROXY") == "1";

    private static string ApiUrl => Environment.GetEnvironmentVariable("CHAOS_TOXIPROXY_API") ?? "http://localhost:8474";

    /// <summary>Where the proxy forwards to, as seen from inside the Toxiproxy container</summary>
    private static string Upstream =>
        Environment.GetEnvironmentVariable("CHAOS_TOXIPROXY_UPSTREAM") ?? "host.docker.internal:5433";

    public static string PostgresConnectionString => new NpgsqlConnectionStringBuilder(Node.PostgresConnectionString)
    {
        Host = "localhost",
        Port = ListenPort
    }.ConnectionString;

    private static readonly HttpClient _client = new();

    public static async Task CreateProxyAsync()
    {
        await _client.DeleteAsync($"{ApiUrl}/proxies/{ProxyName}");
        var response = await _client.PostAsJsonAsync($"{ApiUrl}/proxies", new
        {
            name = ProxyName,
            listen = $"0.0.0.0:{ListenPort}",
            upstream = Upstream,
            enabled = true
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Adds one toxic, waits, then removes it. Returns what it did, for schedule.log</summary>
    public static async Task<string> InjectAsync(Random random)
    {
        var seconds = random.Next(3, 10);
        object toxic;
        string description;

        switch (random.Next(0, 3))
        {
            case 0:
            {
                var latency = random.Next(1000, 6000);
                toxic = new
                {
                    name = "chaos", type = "latency", stream = "downstream", toxicity = 1.0,
                    attributes = new { latency, jitter = latency / 4 }
                };
                description = $"{latency}ms latency";
                break;
            }

            case 1:
                // Connections are reset as they send data, which lands on commits as often as anything else
                toxic = new
                {
                    name = "chaos", type = "reset_peer", stream = "upstream", toxicity = 0.5,
                    attributes = new { timeout = 0 }
                };
                description = "reset_peer on half the traffic";
                break;

            default:
                // A black hole: data is dropped and the connection is closed after the timeout
                toxic = new
                {
                    name = "chaos", type = "timeout", stream = "downstream", toxicity = 1.0,
                    attributes = new { timeout = 4000 }
                };
                description = "timeout (black hole, closed after 4s)";
                break;
        }

        var response = await _client.PostAsJsonAsync($"{ApiUrl}/proxies/{ProxyName}/toxics", toxic);
        response.EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await _client.DeleteAsync($"{ApiUrl}/proxies/{ProxyName}/toxics/chaos");

        return $"{description} for {seconds}s";
    }

    public static async Task RemoveToxicsAsync()
    {
        await _client.DeleteAsync($"{ApiUrl}/proxies/{ProxyName}/toxics/chaos");
    }
}
