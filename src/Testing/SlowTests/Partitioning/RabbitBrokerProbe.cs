using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SlowTests.Partitioning;

/// <summary>
/// GH-3713. Reads the broker's own view of the slot queues through the RabbitMQ management API.
/// </summary>
/// <remarks>
/// <para>The documentation's duplicate claim is stated in terms of the <b>prefetch depth</b> -- the unacked
/// window. That is a broker-side quantity, and nothing inside Wolverine reports it: an unacknowledged delivery
/// that no handler has started yet is by construction invisible to the handler. So the only way to compare a
/// measured duplicate rate against the claimed bound honestly is to ask the broker.</para>
///
/// <para><c>messages_unacknowledged</c> is the number of deliveries the broker has handed out and is still
/// waiting to be settled -- exactly the population that gets requeued when a node dies.
/// <c>messages_ready</c> is the backlog nobody has been given yet, which is what proves the flood was
/// actually a flood rather than a trickle the cluster kept up with.</para>
///
/// <para>The management plugin is part of the <c>rabbitmq:4-management</c> image the repo's compose file
/// pins, so this works in CI on the same container the tests already require. A probe that cannot reach the
/// API returns nulls rather than throwing -- a broken measurement must not fail an invariant test.</para>
/// </remarks>
internal sealed class RabbitBrokerProbe : IDisposable
{
    private readonly HttpClient _client;
    private readonly string[] _queueNames;

    public RabbitBrokerProbe(string baseName, int slotCount, string host = "localhost", int managementPort = 15672)
    {
        _queueNames = Enumerable.Range(1, slotCount).Select(i => $"{baseName}{i}").ToArray();

        _client = new HttpClient { BaseAddress = new Uri($"http://{host}:{managementPort}/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("guest:guest")));
        _client.Timeout = TimeSpan.FromSeconds(5);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// A point-in-time reading across every slot queue. Null when the broker could not be reached.
    /// </summary>
    public async Task<BrokerReading?> ReadAsync(CancellationToken token = default)
    {
        var ready = 0;
        var unacked = 0;

        foreach (var queue in _queueNames)
        {
            try
            {
                using var response = await _client.GetAsync($"api/queues/%2F/{queue}", token);
                if (!response.IsSuccessStatusCode) return null;

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));

                ready += readInt(document.RootElement, "messages_ready");
                unacked += readInt(document.RootElement, "messages_unacknowledged");
            }
            catch (Exception)
            {
                return null;
            }
        }

        return new BrokerReading(ready, unacked);
    }

    private static int readInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>
    /// Force the broker to drop every connection whose client-provided name is
    /// <paramref name="clientName" />, and report how many it dropped.
    /// </summary>
    /// <remarks>
    /// <para><b>This is what makes a hard kill hard.</b> Disposing the <c>IHost</c> is not a kill:
    /// <c>WolverineRuntime</c>'s <c>DisposeAsync</c> calls <c>StopAsync</c> when the runtime has not already
    /// stopped, so the listeners <i>drain</i> -- in-flight handlers finish and their acks go out. A node that
    /// shut itself down tidily is a rolling deploy, not a crash, and measuring one while calling it the other
    /// is how a suite ends up reporting a number that is true of a scenario nobody asked about.</para>
    ///
    /// <para>Closing the connection from the broker side is the real thing. Every unacknowledged delivery on
    /// it is requeued immediately, and a handler that was mid-flight completes into a channel that no longer
    /// exists -- so its work happened but its acknowledgement never lands. That is precisely the situation a
    /// duplicate execution comes from, and nothing short of it produces one.</para>
    /// </remarks>
    public async Task<int> ForceCloseConnectionsAsync(string clientName, CancellationToken token = default)
    {
        var closed = 0;

        try
        {
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
        }
        catch (Exception)
        {
            return closed;
        }

        return closed;
    }

    /// <summary>
    /// Poll until the ready backlog reaches <paramref name="depth" />, so that chaos is introduced against a
    /// genuinely saturated cluster. Returns the reading that satisfied it, or null on timeout.
    /// </summary>
    public async Task<BrokerReading?> WaitForBacklogAsync(int depth, TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var reading = await ReadAsync(token);
            if (reading is not null && reading.Ready >= depth) return reading;

            await Task.Delay(200, token);
        }

        return null;
    }

    internal sealed record BrokerReading(int Ready, int Unacknowledged);
}
