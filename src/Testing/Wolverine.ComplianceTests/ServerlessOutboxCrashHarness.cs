using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Transports.Tcp;

namespace Wolverine.ComplianceTests.Serverless;

// PR #5044 exploratory testing (GH-5016, DurabilityMode.ServerlessWithOutbox). Drives the out-of-solution
// src/Testing/ServerlessOutboxCrashHost console app as a "function instance" that a test kills with SIGKILL.

/// <summary>
///     The receiving side's view of the crash host's ExplorePing, mapped by message identity
/// </summary>
[MessageIdentity("explore5044-ping")]
public record ExploreReceivedPing(Guid Id);

public class PingCounter
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public void Record(Guid id) => _counts.AddOrUpdate(id, 1, (_, x) => x + 1);

    public int CountFor(Guid id) => _counts.TryGetValue(id, out var count) ? count : 0;

    public int Total => _counts.Values.Sum();

    public IReadOnlyDictionary<Guid, int> Snapshot() => new Dictionary<Guid, int>(_counts);

    public async Task<bool> WaitForAsync(Func<PingCounter, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition(this)) return true;
            await Task.Delay(50);
        }

        return condition(this);
    }
}

public static class ExploreReceivedPingHandler
{
    public static void Handle(ExploreReceivedPing ping, PingCounter counter) => counter.Record(ping.Id);
}

/// <summary>
///     A plain Wolverine host listening on a TCP port and counting every ExplorePing it handles, by id
/// </summary>
public sealed class PingReceiver : IAsyncDisposable
{
    private PingReceiver(IHost host, int port)
    {
        Host = host;
        Port = port;
        Counter = host.Services.GetRequiredService<PingCounter>();
    }

    public IHost Host { get; }
    public int Port { get; }
    public PingCounter Counter { get; }

    public static async Task<PingReceiver> StartAsync(int port)
    {
        var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExploreReceivedPingHandler));
                opts.Services.AddSingleton<PingCounter>();
                opts.ListenAtPort(port);
            }).StartAsync();

        return new PingReceiver(host, port);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>
///     Accepts TCP connections and reads whatever is sent, but never answers: a broker that froze, or a function
///     frozen in the middle of its send. Signals once the first bytes arrive
/// </summary>
public sealed class Blackhole : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<TcpClient> _clients = new();
    private int _connections;

    public Blackhole(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = port;
        _ = acceptLoopAsync();
    }

    public int Port { get; }

    public int Connections => _connections;

    public TaskCompletionSource FirstData { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task acceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (Exception)
            {
                return;
            }

            lock (_clients) _clients.Add(client);
            Interlocked.Increment(ref _connections);
            _ = drainAsync(client);
        }
    }

    private async Task drainAsync(TcpClient client)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var stream = client.GetStream();
            while (!_cancellation.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _cancellation.Token);
                if (read == 0) return;
                FirstData.TrySetResult();
            }
        }
        catch (Exception)
        {
            // gone
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
        }
    }
}

/// <summary>
///     A loopback TCP proxy that holds every reply from upstream for <see cref="Delay" /> before passing it on: a
///     slow broker, so that a durability pass stays inside its send long enough for another pass to overlap it
/// </summary>
public sealed class SlowReplyProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<TcpClient> _sockets = new();

    public SlowReplyProxy(int port, int upstreamPort, TimeSpan delay)
    {
        _upstreamPort = upstreamPort;
        Delay = delay;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = acceptLoopAsync();
    }

    public TimeSpan Delay { get; }

    public int Connections;

    private async Task acceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref Connections);
            _ = relayAsync(client);
        }
    }

    private async Task relayAsync(TcpClient client)
    {
        using var server = new TcpClient();
        lock (_sockets)
        {
            _sockets.Add(client);
            _sockets.Add(server);
        }

        try
        {
            await server.ConnectAsync(IPAddress.Loopback, _upstreamPort, _cancellation.Token);
            var toServer = pumpAsync(client.GetStream(), server.GetStream(), TimeSpan.Zero);
            var toClient = pumpAsync(server.GetStream(), client.GetStream(), Delay);
            await Task.WhenAny(toServer, toClient);
        }
        catch (Exception)
        {
            // either side went away
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task pumpAsync(NetworkStream from, NetworkStream to, TimeSpan delay)
    {
        var buffer = new byte[64 * 1024];
        while (!_cancellation.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buffer, _cancellation.Token);
            if (read == 0) return;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _cancellation.Token);
            await to.WriteAsync(buffer.AsMemory(0, read), _cancellation.Token);
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        lock (_sockets)
        {
            foreach (var socket in _sockets) socket.Dispose();
        }
    }
}

/// <summary>
///     One run of the ServerlessOutboxCrashHost console app
/// </summary>
public sealed class CrashHostProcess : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly StringBuilder _all = new();
    private readonly string _logFile;

    private CrashHostProcess(Process process, string label)
    {
        _process = process;
        _logFile = Path.Combine(CrashHost.LogDirectory, $"{DateTime.Now:HHmmss}-{label}-{process.Id}.log");
    }

    public int Pid => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get
        {
            lock (_all) return _all.ToString();
        }
    }

    public IReadOnlyList<string> Markers => _lines.ToArray();

    internal static CrashHostProcess Start(string label, string[] args, IDictionary<string, string>? env)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(CrashHost.DllPath);
        foreach (var arg in args) info.ArgumentList.Add(arg);

        if (env != null)
        {
            foreach (var pair in env) info.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        CrashHostProcess? wrapper = null;

        process.OutputDataReceived += (_, e) => wrapper?.receive(e.Data);
        process.ErrorDataReceived += (_, e) => wrapper?.receive(e.Data);

        process.Start();
        wrapper = new CrashHostProcess(process, label);
        wrapper.receive($"# {label}: dotnet {string.Join(' ', info.ArgumentList)}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return wrapper;
    }

    private void receive(string? line)
    {
        if (line == null) return;

        lock (_all) _all.AppendLine(line);
        if (line.StartsWith("@@")) _lines.Enqueue(line);

        try
        {
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // logging only
        }
    }

    public async Task<string> WaitForMarkerAsync(string prefix, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = _lines.FirstOrDefault(x => x.StartsWith(prefix));
            if (line != null) return line;

            if (_process.HasExited)
            {
                // Let the output drain
                await Task.Delay(200);
                line = _lines.FirstOrDefault(x => x.StartsWith(prefix));
                if (line != null) return line;

                throw new Exception($"Crash host exited ({_process.ExitCode}) before writing '{prefix}'.\n{Output}");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Crash host did not write '{prefix}' within {timeout}.\n{Output}");
    }

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Crash host did not exit within {timeout}.\n{Output}");
        }

        // Let the redirected output drain
        _process.WaitForExit();
    }

    /// <summary>
    ///     SIGKILL, no shutdown of any kind: what a frozen-then-reclaimed function instance looks like
    /// </summary>
    public async Task KillAsync()
    {
        if (!_process.HasExited)
        {
            receive($"# SIGKILL {_process.Id} at {DateTimeOffset.UtcNow:O}");
            _process.Kill(entireProcessTree: false);
        }

        await _process.WaitForExitAsync();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: false);
            }
            catch (Exception)
            {
                // already gone
            }
        }

        _process.Dispose();
    }
}

public static class CrashHost
{
    private static readonly Lazy<string> _repositoryRoot = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "wolverine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Unable to find the repository root");
    });

    public static string DllPath
    {
        get
        {
            var path = Path.Combine(_repositoryRoot.Value, "src", "Testing", "ServerlessOutboxCrashHost", "bin", "Release",
                "net9.0", "ServerlessOutboxCrashHost.dll");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Build the crash host first: dotnet build src/Testing/ServerlessOutboxCrashHost -c Release -f net9.0 (looked for {path})");
            }

            return path;
        }
    }

    public static string LogDirectory
    {
        get
        {
            var directory = Path.Combine(Directory.GetParent(_repositoryRoot.Value)!.FullName, "explore5044-rec-logs",
                "children");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static CrashHostProcess Start(string label, string[] args, IDictionary<string, string>? env = null)
    {
        return CrashHostProcess.Start(label, args, env);
    }

    /// <summary>
    ///     Run the crash host to completion and fail on a non-zero exit
    /// </summary>
    public static async Task<CrashHostProcess> RunAsync(string label, string[] args,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var process = Start(label, args, env);
        await process.WaitForExitAsync(timeout ?? 2.Minutes());
        if (process.ExitCode != 0)
        {
            throw new Exception($"Crash host '{label}' exited with {process.ExitCode}.\n{process.Output}");
        }

        return process;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static Guid Derive(Guid seed, int index)
    {
        var bytes = seed.ToByteArray();
        var suffix = BitConverter.GetBytes(index);
        for (var i = 0; i < 4; i++) bytes[12 + i] = suffix[i];
        return new Guid(bytes);
    }

    /// <summary>
    ///     Parses "@@PASS sent=1 failed=0 ..." into its numbers
    /// </summary>
    public static Dictionary<string, int> ParsePass(string marker)
    {
        return marker.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => x.Split('='))
            .ToDictionary(x => x[0], x => int.Parse(x[1]));
    }
}
