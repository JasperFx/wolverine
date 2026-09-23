using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// Asks the broker things AMQP will not answer: what a queue actually is (GH-4559), and -- by dropping a
/// node's connections -- how it behaves when killed the way a crash kills it (GH-4095).
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

    /// <summary>
    /// GH-4559. A probe that is known to be talking to something. The management plugin ships in the
    /// <c>rabbitmq:4-management</c> image the repo's compose file pins, so an unreachable API is a broken
    /// environment rather than a reason to skip -- and a test that skips here quietly goes back to
    /// proving nothing.
    /// </summary>
    public static async Task<RabbitManagementProbe> RequireAsync(CancellationToken token = default)
    {
        var probe = new RabbitManagementProbe();
        if (await probe.IsAvailableAsync(token)) return probe;

        probe.Dispose();
        throw new InvalidOperationException(
            "The RabbitMQ management API is not reachable on localhost:15672, so the broker cannot be asked what it actually has.");
    }

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
    /// GH-4559. The queue type the BROKER reports -- "classic", "quorum" or "stream" -- or null when the
    /// broker has no queue by that name.
    /// </summary>
    /// <remarks>
    /// Read off the top-level <c>type</c> rather than the <c>x-queue-type</c> argument. A queue declared
    /// without the argument is classic, so the argument's absence has to be interpreted, whereas
    /// <c>type</c> is the effective answer for every queue.
    /// </remarks>
    public async Task<string?> GetQueueTypeAsync(string queueName, string vhost = "/",
        CancellationToken token = default)
    {
        using var document = await getQueueAsync(queueName, vhost, token);
        if (document is null) return null;

        if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() is { } value)
        {
            return value;
        }

        // Older management plugins did not report the effective type, so fall back to the argument --
        // where an absent argument really does mean classic
        if (document.RootElement.TryGetProperty("arguments", out var arguments)
            && arguments.TryGetProperty("x-queue-type", out var argument)
            && argument.GetString() is { } declared)
        {
            return declared;
        }

        return "classic";
    }

    /// <summary>
    /// GH-4559. The arguments the BROKER holds against a queue -- <c>x-dead-letter-exchange</c> and
    /// friends -- or null when the broker has no queue by that name. Asserting
    /// <c>RabbitMqQueue.Arguments</c> instead only reads back the dictionary Wolverine assembled to PASS
    /// to <c>QueueDeclareAsync</c>, which is true whether or not the declaration ever reached Rabbit.
    /// </summary>
    public async Task<Dictionary<string, string>?> GetQueueArgumentsAsync(string queueName,
        string vhost = "/", CancellationToken token = default)
    {
        using var document = await getQueueAsync(queueName, vhost, token);
        if (document is null) return null;

        var arguments = new Dictionary<string, string>();
        if (document.RootElement.TryGetProperty("arguments", out var element))
        {
            foreach (var property in element.EnumerateObject())
            {
                arguments[property.Name] = property.Value.ToString();
            }
        }

        return arguments;
    }

    /// <summary>
    /// GH-4559. The exchanges the BROKER says are bound to this queue.
    /// </summary>
    public async Task<string[]> GetBoundExchangesAsync(string queueName, string vhost = "/",
        CancellationToken token = default)
    {
        using var response = await _client.GetAsync(
            $"api/queues/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(queueName)}/bindings", token);

        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));

        return document.RootElement.EnumerateArray()
            .Select(x => x.TryGetProperty("source", out var source) ? source.GetString() : null)
            // Every queue has an implicit binding from the default exchange, which reports an empty
            // source. That one is the broker's, not something Wolverine declared
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();
    }

    /// <summary>
    /// GH-4559. Whether the BROKER has an exchange by this name, as opposed to whether Wolverine's
    /// <c>RabbitMqTransport.Exchanges</c> cache has an entry for one.
    /// </summary>
    public async Task<bool> ExchangeExistsAsync(string exchangeName, string vhost = "/",
        CancellationToken token = default)
    {
        using var response = await _client.GetAsync(
            $"api/exchanges/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(exchangeName)}", token);

        if (response.StatusCode == HttpStatusCode.NotFound) return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// There is no AMQP way to ask any of this. <c>QueueDeclarePassiveAsync</c> answers with only the
    /// name, message count and consumer count, so the arguments a queue was actually created with are
    /// never on the wire; the management plugin is the only thing that will say.
    /// </summary>
    private async Task<JsonDocument?> getQueueAsync(string queueName, string vhost, CancellationToken token)
    {
        using var response = await _client.GetAsync(
            $"api/queues/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(queueName)}", token);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
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
