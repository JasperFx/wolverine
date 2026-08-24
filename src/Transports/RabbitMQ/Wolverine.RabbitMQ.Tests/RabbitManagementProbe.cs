using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4095. Kills a node the way a crash does, by asking the broker to drop its connections.
/// </summary>
/// <remarks>
/// <para>Disposing an <c>IHost</c> is not a kill. <c>WolverineRuntime.DisposeAsync</c> calls
/// <c>StopAsync</c> when the runtime has not already stopped, so listeners drain, in-flight handlers
/// are awaited, and their acknowledgements go out. A node that shut itself down tidily is a rolling
/// deploy, not a crash.</para>
///
/// <para>Closing the connection from the broker side is the real thing: every unacknowledged delivery
/// on it is requeued immediately, and a handler still running completes into a channel that no longer
/// exists, so its work happened but its acknowledgement never lands.</para>
///
/// <para>A trimmed-down sibling of SlowTests' <c>RabbitBrokerProbe</c> from GH-3713, kept here so the
/// per-transport suites get crash coverage on the PR path -- SlowTests runs in no CI workflow. The
/// management plugin ships in the <c>rabbitmq:4-management</c> image the repo's compose file pins.</para>
/// </remarks>
internal sealed class RabbitManagementProbe : IDisposable
{
    private readonly HttpClient _client;

    public RabbitManagementProbe(string host = "localhost", int managementPort = 15672)
    {
        _client = new HttpClient { BaseAddress = new Uri($"http://{host}:{managementPort}/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("guest:guest")));
        _client.Timeout = TimeSpan.FromSeconds(5);
    }

    public void Dispose() => _client.Dispose();

    public async Task<bool> IsAvailableAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await _client.GetAsync("api/overview", token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Poll until the broker lists a connection with this client-provided name. The management plugin's
    /// stats collector lags the actual connection by seconds, so a kill issued the instant traffic flows
    /// finds nothing to close and silently degrades into "no kill happened".
    /// </summary>
    public async Task<bool> WaitForConnectionAsync(string clientName, TimeSpan timeout,
        CancellationToken token = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await HasConnectionAsync(clientName, token)) return true;
            await Task.Delay(250, token);
        }

        return false;
    }

    public async Task<bool> HasConnectionAsync(string clientName, CancellationToken token = default)
    {
        try
        {
            using var response = await _client.GetAsync("api/connections", token);
            if (!response.IsSuccessStatusCode) return false;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var connection in document.RootElement.EnumerateArray())
            {
                if (!connection.TryGetProperty("client_properties", out var properties)) continue;
                if (!properties.TryGetProperty("connection_name", out var name)) continue;
                if (name.GetString() == clientName) return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Drop every connection whose client-provided name matches, and report how many were dropped.
    /// </summary>
    public async Task<int> ForceCloseConnectionsAsync(string clientName, CancellationToken token = default)
    {
        var closed = 0;

        using var response = await _client.GetAsync("api/connections", token);
        if (!response.IsSuccessStatusCode) return 0;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));

        foreach (var connection in document.RootElement.EnumerateArray())
        {
            if (!connection.TryGetProperty("client_properties", out var properties)) continue;
            if (!properties.TryGetProperty("connection_name", out var name)) continue;
            if (name.GetString() != clientName) continue;

            var id = connection.GetProperty("name").GetString();
            if (id is null) continue;

            using var delete = await _client.DeleteAsync($"api/connections/{Uri.EscapeDataString(id)}", token);
            if (delete.IsSuccessStatusCode) closed++;
        }

        return closed;
    }
}
