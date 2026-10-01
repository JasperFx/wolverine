using Alba;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4742. A defect in the endpoint's own [DeduplicatedWithResponse] fails when the chain is built, at startup; a
/// host that cannot store the claims only warns, and the endpoint throws at its first request.
/// </summary>
public class deduplicated_with_response_declaration
{
    [Fact]
    public void no_scope_is_refused_at_startup()
    {
        Should.Throw<InvalidOperationException>(() => HttpChain.ChainFor(typeof(UnscopedDeduplicatedResponse), "Post"))
            .Message.ShouldContain("needs a DeduplicationScope");
    }

    [Fact]
    public void combining_with_deduplicated_is_refused_at_startup()
    {
        Should.Throw<InvalidOperationException>(() => HttpChain.ChainFor(typeof(DoublyDeduplicatedResponse), "Post"))
            .Message.ShouldContain("both [Deduplicated] and [DeduplicatedWithResponse]");
    }

    [Fact]
    public void an_endpoint_with_no_resource_is_refused_at_startup()
    {
        Should.Throw<NotSupportedException>(() => HttpChain.ChainFor(typeof(ResourcelessDeduplicatedResponse), "Post"))
            .Message.ShouldContain("returns no resource");
    }

    [Fact]
    public void a_negative_window_is_refused_at_startup()
    {
        Should.Throw<InvalidOperationException>(() =>
                HttpChain.ChainFor(typeof(NegativeWindowDeduplicatedResponse), "Post"))
            .Message.ShouldContain("WindowInSeconds = -1");
    }

    [Fact]
    public async Task the_refusal_codes_are_in_the_endpoint_metadata()
    {
        await using var host = await startAsync(new CapturingLoggerProvider());

        var codes = host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == "/declaration/storeless")
            .Metadata.OfType<IProducesResponseTypeMetadata>().Select(x => x.StatusCode).ToArray();

        codes.ShouldContain(400);
        codes.ShouldContain(409);
        codes.ShouldContain(422);
    }

    [Fact]
    public async Task without_the_opt_in_the_host_warns_and_the_first_request_throws()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/storeless") && x.Contains("EnableDeduplicatedResponses is off"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => host.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("storeless")).ToUrl("/declaration/storeless");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
        }));
        ex.Message.ShouldContain("EnableDeduplicatedResponses = true");
    }

    [Fact]
    public async Task a_correctly_configured_host_does_not_warn()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true);

        logs.Warnings.ShouldNotContain(x => x.Contains("[DeduplicatedWithResponse]"));
    }

    [Fact]
    public async Task without_a_message_store_the_host_warns()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/storeless") && x.Contains("does not implement IReplayableDeduplicationStore"));
    }

    private static async Task<IAlbaHost> startAsync(CapturingLoggerProvider logs, bool persist = false,
        bool enable = false)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Logging.AddProvider(logs);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            if (persist)
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_deduplicated_declaration");
                opts.Durability.EnableDeduplicatedResponses = enable;
            }

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_declaration).Assembly);
        });

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the endpoint under test", type => type != typeof(StorelessDeduplicatedResponse)))));

        if (persist) await ((IHost)host).ResetResourceState();

        return host;
    }

    private class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings) return _warnings.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private class CapturingLogger(CapturingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Warning) return;
                lock (parent._warnings) parent._warnings.Add(formatter(state, exception));
            }
        }
    }
}

// The four below are invalid on purpose. Hidden from discovery, so no other host scanning this assembly fails
// to start; the tests build their chains directly.

[WolverineIgnore]
public static class UnscopedDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.None)]
    [WolverinePost("/declaration/unscoped")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

[WolverineIgnore]
public static class DoublyDeduplicatedResponse
{
    [Deduplicated]
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/doubly")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

[WolverineIgnore]
public static class ResourcelessDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/resourceless")]
    public static void Post(DeduplicatedOrder request)
    {
    }
}

[WolverineIgnore]
public static class NegativeWindowDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User, WindowInSeconds = -1)]
    [WolverinePost("/declaration/negative-window")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

public static class StorelessDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/storeless")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}
