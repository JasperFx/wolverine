using Alba;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.Runtime;
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
    public async Task without_the_opt_in_the_host_warns_and_the_first_request_fails()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/storeless") && x.Contains("EnableDeduplicatedResponses is off"));

        // The endpoint fails on its first claim. Whether that reaches the caller as an exception or as a 500
        // depends on the hosting environment -- WebApplication installs the developer exception page when the
        // environment is Development, which CI sets -- so assert that the request did not succeed...
        try
        {
            var result = await host.Scenario(x =>
            {
                x.Post.Json(new DeduplicatedOrder("storeless")).ToUrl("/declaration/storeless");
                x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
                x.IgnoreStatusCode();
            });

            result.Context.Response.StatusCode.ShouldBeGreaterThanOrEqualTo(500);
        }
        catch (InvalidOperationException)
        {
            // Propagated straight through the test server: no exception page is installed.
        }

        // ...and assert the message naming the way out where it is actually raised.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await host.Services.GetRequiredService<DeduplicatedResponses>()
                .TryClaimAsync("declaration-storeless", "fingerprint", Guid.NewGuid().ToString(), null, null));

        ex.Message.ShouldContain("EnableDeduplicatedResponses = true");
    }

    [Fact]
    public async Task a_correctly_configured_host_does_not_warn_about_storage()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true);

        // Scoped to the storage diagnostic deliberately: this endpoint is anonymous, so the separate
        // anonymous-User-scope warning (a_user_scope_on_an_anonymous_endpoint_warns) does fire for it.
        logs.Warnings.ShouldNotContain(x => x.Contains("EnableDeduplicatedResponses is off"));
        logs.Warnings.ShouldNotContain(x => x.Contains("does not implement IReplayableDeduplicationStore"));
    }

    [Fact]
    public async Task without_a_message_store_the_host_warns()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/storeless") && x.Contains("does not implement IReplayableDeduplicationStore"));
    }

    [Fact]
    public void a_form_bound_endpoint_is_refused_at_startup()
    {
        // GH-4742. Form values are kept as variables whose creator frames are never in Middleware, so the
        // arranger hoists the form read ahead of EnableRequestBufferingFrame; the fingerprint then throws on
        // an unseekable body for EVERY request. Refused while the chain is built, not at the first request.
        Should.Throw<NotSupportedException>(() => HttpChain.ChainFor(typeof(FormBoundDeduplicatedResponse), "Post"))
            .Message.ShouldContain("form-encoded requests are not supported");
    }

    [Fact]
    public void a_bound_form_type_is_refused_at_startup_too()
    {
        // The other form shape: [FromForm] on a whole type rather than on a simple value.
        Should.Throw<NotSupportedException>(() =>
                HttpChain.ChainFor(typeof(BoundFormTypeDeduplicatedResponse), "Post"))
            .Message.ShouldContain("form-encoded requests are not supported");
    }

    [Fact]
    public void a_file_upload_endpoint_is_not_refused()
    {
        // Multipart/file bindings put their frames IN Middleware, so the buffering still comes first. They
        // must keep working -- the refusal above is deliberately narrower than HttpChain.IsFormData.
        var chain = HttpChain.ChainFor(typeof(FileUploadDeduplicatedResponse), "Post");

        chain.IsFormData.ShouldBeTrue();
        chain.DeduplicatedWithResponse.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_user_scope_on_an_anonymous_endpoint_warns()
    {
        // GH-4742. Scope.None is refused outright; an unauthenticated caller makes Scope.User produce the
        // same key, so the same hole is reachable through a route the refusal does not cover. A warning
        // rather than a refusal: an endpoint authenticated by an upstream gateway is legitimate.
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/storeless") && x.Contains("no authorization metadata"));
    }

    [Fact]
    public async Task a_user_scope_on_an_authorized_endpoint_does_not_warn()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true,
            endpoint: typeof(AuthorizedDeduplicatedResponse));

        // Not vacuous: the endpoint really is in the graph, and it really is User-scoped.
        var chain = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .Chains.Single(x => x.RoutePattern!.RawText == "/declaration/authorized");
        chain.DeduplicatedWithResponse!.Scope.ShouldBe(DeduplicationScope.User);

        logs.Warnings.ShouldNotContain(x => x.Contains("no authorization metadata"));
    }

    [Fact]
    public async Task a_user_scope_under_a_fallback_authorization_policy_does_not_warn()
    {
        // The policy authorizes every endpoint that declares nothing of its own, so there is nothing to declare.
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true, services: requireAuthenticatedUsers);

        logs.Warnings.ShouldNotContain(x => x.Contains("no authorization metadata"));
    }

    [Fact]
    public async Task an_anonymous_endpoint_under_a_fallback_authorization_policy_still_warns()
    {
        // [AllowAnonymous] opts out of the fallback policy.
        var logs = new CapturingLoggerProvider();
        await using var host = await startAsync(logs, persist: true, enable: true,
            endpoint: typeof(AnonymousDeduplicatedResponse), services: requireAuthenticatedUsers);

        logs.Warnings.ShouldContain(x =>
            x.Contains("/declaration/anonymous") && x.Contains("no authorization metadata"));
    }

    private static void requireAuthenticatedUsers(IServiceCollection services)
        => services.AddAuthorization(x =>
            x.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    [Fact]
    public void an_unknown_scope_is_refused_at_startup()
    {
        Should.Throw<InvalidOperationException>(() =>
                HttpChain.ChainFor(typeof(UnknownScopeDeduplicatedResponse), "Post"))
            .Message.ShouldContain("unknown DeduplicationScope value 8");
    }

    [Fact]
    public async Task a_requirement_a_policy_sets_is_validated_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => startAsync(new CapturingLoggerProvider(),
            endpoint: typeof(PolicyDeduplicatedResponse),
            configure: chain => chain.DeduplicatedWithResponse =
                new DeduplicatedWithResponseRequirement { Scope = DeduplicationScope.None }));

        ex.Message.ShouldContain("needs a DeduplicationScope");
    }

    [Fact]
    public async Task a_requirement_a_policy_sets_is_in_the_endpoint_metadata()
    {
        await using var host = await startAsync(new CapturingLoggerProvider(),
            endpoint: typeof(PolicyDeduplicatedResponse),
            configure: chain => chain.DeduplicatedWithResponse =
                new DeduplicatedWithResponseRequirement { Scope = DeduplicationScope.User });

        var codes = host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == "/declaration/policy")
            .Metadata.OfType<IProducesResponseTypeMetadata>().Select(x => x.StatusCode).ToArray();

        codes.ShouldContain(409);
        codes.ShouldContain(422);
    }

    private static async Task<IAlbaHost> startAsync(CapturingLoggerProvider logs, bool persist = false,
        bool enable = false, Type? endpoint = null, Action<HttpChain>? configure = null, Action<IServiceCollection>? services = null)
    {
        endpoint ??= typeof(StorelessDeduplicatedResponse);

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
        services?.Invoke(builder.Services);

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the endpoint under test", type => type != endpoint));

            if (configure != null) opts.ConfigureEndpoints(configure);
        }));

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

[WolverineIgnore]
public static class UnknownScopeDeduplicatedResponse
{
    [DeduplicatedWithResponse((DeduplicationScope)8)]
    [WolverinePost("/declaration/unknown-scope")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

// No attribute: the tests' policy sets the requirement.
public static class PolicyDeduplicatedResponse
{
    [WolverinePost("/declaration/policy")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

[WolverineIgnore]
public static class FormBoundDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/form-value")]
    public static DeduplicatedOrderCreated Post([FromForm] string name) => new(Guid.NewGuid());
}

[WolverineIgnore]
public static class BoundFormTypeDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/form-type")]
    public static DeduplicatedOrderCreated Post([FromForm] DeduplicatedFormBody body) => new(Guid.NewGuid());
}

public class DeduplicatedFormBody
{
    public string Name { get; set; } = string.Empty;
}

// Files bind through Middleware frames, so the request buffering still comes first: NOT refused.
[WolverineIgnore]
public static class FileUploadDeduplicatedResponse
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/file")]
    public static DeduplicatedOrderCreated Post(IFormFile file) => new(Guid.NewGuid());
}

// Authorized, so the anonymous-User-scope warning must NOT fire for it.
public static class AuthorizedDeduplicatedResponse
{
    [Authorize]
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/authorized")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

// Opts out of any fallback authorization policy, so the anonymous-User-scope warning must still fire for it.
public static class AnonymousDeduplicatedResponse
{
    [AllowAnonymous]
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/declaration/anonymous")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}
