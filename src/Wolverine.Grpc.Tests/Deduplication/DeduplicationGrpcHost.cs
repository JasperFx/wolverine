using JasperFx;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Wolverine.Persistence;

namespace Wolverine.Grpc.Tests.Deduplication;

/// <summary>
///     GH-4180. In-process ASP.NET Core + Wolverine gRPC host for the deduplication tests.
///
///     <para>
///     Deliberately runs against a stub <see cref="IMessageDeduplicator" /> rather than a real message
///     store. The durable half — the INSERT that either succeeds or trips the primary key, the reaper,
///     the retention window — is covered against a real PostgreSQL database in
///     <c>PostgresqlTests.logical_message_deduplication</c>. What is unproven for gRPC, and what these
///     tests exist for, is the generated code: that the id is read out of request metadata, that the
///     claim gates the forward, and that a failure releases. Standing up Postgres here would slow the
///     gRPC CI job down to test something already tested elsewhere.
///     </para>
/// </summary>
public sealed class DeduplicationGrpcHost : IAsyncDisposable
{
    private WebApplication? _app;

    public GrpcChannel? Channel { get; private set; }

    public StubMessageDeduplicator Deduplicator { get; } = new();

    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Host has not been started yet.");

    public static async Task<DeduplicationGrpcHost> StartAsync()
    {
        var host = new DeduplicationGrpcHost();

        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(DeduplicationGrpcHost).Assembly;
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableMessageDeduplication = true;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(DedupEchoHandler));

            opts.Services.AddSingleton<IMessageDeduplicator>(host.Deduplicator);
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        host._app = builder.Build();

        host._app.UseRouting();
        host._app.MapWolverineGrpcServices();

        await host._app.StartAsync();

        var handler = host._app.GetTestServer().CreateHandler();
        host.Channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        return host;
    }

    public TService CreateClient<TService>() where TService : class
        => Channel!.CreateGrpcService<TService>();

    public async ValueTask DisposeAsync()
    {
        Channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

/// <summary>
///     Records every claim and release so the tests can assert on what the GENERATED code did, not
///     merely on what the caller saw. A test that only checked the RPC status would pass over a
///     generated method that never called the deduplicator at all.
/// </summary>
public sealed class StubMessageDeduplicator : IMessageDeduplicator
{
    private readonly HashSet<string> _claimed = [];

    public List<string> Claims { get; } = [];
    public List<string> Releases { get; } = [];

    public ValueTask<bool> TryClaimAsync(string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation)
    {
        lock (_claimed)
        {
            Claims.Add(deduplicationId);
            return new ValueTask<bool>(_claimed.Add(deduplicationId));
        }
    }

    public ValueTask ReleaseAsync(string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation)
    {
        lock (_claimed)
        {
            Releases.Add(deduplicationId);
            _claimed.Remove(deduplicationId);
        }

        return ValueTask.CompletedTask;
    }
}
