using System.Security.Claims;
using Alba;
using IntegrationTests;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Polecat;
using Wolverine.Runtime;

namespace PolecatTests.Http;

/// <summary>
/// GH-4741, the Polecat mirror of <c>Wolverine.Http.Tests/user_name_relay_into_marten_session</c>.
/// <c>EnableRelayOfUserName</c> is supposed to land on <c>IDocumentSession.LastModifiedBy</c>, and it did not
/// from any HTTP endpoint on any store: the old <c>UserNamePolicy</c> skipped session-only chains outright,
/// and even where it fired, the session was opened by a hoisted <c>IVariableSource</c> frame that ran before
/// the policy's inserted middleware. Polecat uses the same <c>SessionVariableSource</c> shape as Marten, so
/// the fix covers it -- this is what proves that rather than assuming it.
/// </summary>
public class user_name_relay_into_polecat_session : IAsyncLifetime
{
    private const string TheUserName = "auditor@example.com";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.EnableRelayOfUserName = true;

            opts.Discovery.DisableConventionalDiscovery();

            // Wolverine caches the detected application assembly process-wide, so a host built after
            // another fixture in this assembly inherits THAT application assembly and never sees the
            // endpoints below -- every request 404s. Passes in isolation, fails in the full suite.
            opts.Discovery.IncludeAssembly(typeof(user_name_relay_into_polecat_session).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "http_user_name_relay";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a user name relay test endpoint",
                    type => type != typeof(PolecatUserNameEndpoint)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private static ClaimsPrincipal authenticatedUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, TheUserName)], "TestAuth"));
    }

    [Fact]
    public async Task relays_the_user_name_to_a_session_only_chain()
    {
        var result = await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = authenticatedUser());
            x.Get.Url("/polecat-user-name/session");
        });

        (await result.ReadAsTextAsync()).ShouldBe(TheUserName);
    }

    [Fact]
    public async Task relays_the_user_name_to_a_chain_with_both_a_bus_and_a_session()
    {
        var result = await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = authenticatedUser());
            x.Get.Url("/polecat-user-name/session-and-context");
        });

        (await result.ReadAsTextAsync()).ShouldBe($"{TheUserName}|{TheUserName}");
    }

    [Fact]
    public async Task the_relay_is_emitted_before_the_session_is_opened()
    {
        // Warm the chain so codegen has run, then read the ordering off the generated source -- the
        // behavioural tests above cannot see it, and the ordering is the half of GH-4741 that a future
        // refactoring could silently undo.
        await theHost.Scenario(x => x.Get.Url("/polecat-user-name/session"));

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == "/polecat-user-name/session");

        var source = chain.SourceCode.ShouldNotBeNull();

        var relayIndex = source.IndexOf("UserNameMiddleware.Apply(httpContext, messageContext);",
            StringComparison.Ordinal);
        relayIndex.ShouldBeGreaterThanOrEqualTo(0, $"Source code follows:\n{source}");

        var openSessionIndex = source.IndexOf("OpenSession(", StringComparison.Ordinal);
        openSessionIndex.ShouldBeGreaterThanOrEqualTo(0, $"Source code follows:\n{source}");

        relayIndex.ShouldBeLessThan(openSessionIndex,
            "OutboxedSessionFactory.OpenSession copies context.UserName into session.LastModifiedBy " +
            $"eagerly, so a relay emitted afterwards has no effect at all. Source code follows:\n{source}");
    }
}

public static class PolecatUserNameEndpoint
{
    [WolverineGet("/polecat-user-name/session")]
    public static string SessionOnly(IDocumentSession session)
    {
        return session.LastModifiedBy ?? "NONE";
    }

    [WolverineGet("/polecat-user-name/session-and-context")]
    public static string SessionAndContext(IDocumentSession session, IMessageContext context)
    {
        return $"{context.UserName ?? "NONE"}|{session.LastModifiedBy ?? "NONE"}";
    }
}
