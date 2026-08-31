using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace IntegrationTests;

/// <summary>
/// "Is this emulator up?", for the test suites that skip rather than fail when their emulator is not
/// running. Probed once per host and port per process.
/// </summary>
public static class EmulatorProbe
{
    private static readonly ConcurrentDictionary<string, Lazy<bool>> _probes = new();

    public static bool IsListening(string host, int port)
    {
        return _probes
            .GetOrAdd($"{host}:{port}", _ => new Lazy<bool>(() => probe(host, port)))
            .Value;
    }

    /// <summary>
    /// Tries every address the host resolves to and takes the first that connects.
    /// </summary>
    /// <remarks>
    /// GH-4160. Each suite used to carry its own copy of a single <c>ConnectAsync(host, port)</c>
    /// with a two second budget for the whole call. docker-compose publishes the LocalStack gateway
    /// as <c>127.0.0.1:4566:4566</c>, so <c>::1</c> has no listener, and the IPv6 attempt
    /// <c>localhost</c> resolves to first does not always fail inside that budget: the S3 suite
    /// skipped every test against a LocalStack that was up and reported green. The other three
    /// emulators publish dual-stack today and so happened to be unaffected. Same silent-skip failure
    /// mode as GH-4007.
    /// </remarks>
    private static bool probe(string host, int port)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : Dns.GetHostAddresses(host);
        }
        catch
        {
            return false;
        }

        foreach (var address in addresses)
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                var connect = client.ConnectAsync(address, port);
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                if (connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected)
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
                {
                    return true;
                }
            }
            catch
            {
                // One address family failing says nothing about the next.
            }
        }

        return false;
    }
}
