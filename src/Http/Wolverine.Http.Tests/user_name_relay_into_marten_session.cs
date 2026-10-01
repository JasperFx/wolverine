using System.Security.Claims;
using Alba;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4741. <c>EnableRelayOfUserName</c> never reached <c>IDocumentSession.LastModifiedBy</c> from any HTTP
/// endpoint that opened a store session, which is precisely what <c>docs/guide/http/security.md</c> claims it
/// does. Two independent causes, both covered here:
///
/// <list type="number">
/// <item>
/// The old <c>UserNamePolicy</c> only inserted <c>UserNameMiddleware.Apply</c> into a chain whose
/// <c>ServiceDependencies</c> contained <c>IMessageContext</c> or <c>IMessageBus</c>. A chain that writes
/// only through its session — returning an <c>IMartenOp</c>, or taking an <c>IDocumentSession</c> — declares
/// neither, so no frame was inserted and the relay never ran.
/// </item>
/// <item>
/// Ordering, which broke the <c>IMessageBus</c> shape too. The session is opened by a frame from
/// Wolverine.Marten's <c>SessionVariableSource</c>, and JasperFx's <c>MethodFrameArranger</c> hoists every
/// variable-source frame to the FRONT of the method before the topological sort — so a frame inserted at
/// <c>chain.Middleware[0]</c> cannot precede it. <c>OutboxedSessionFactory.OpenSession</c> copies
/// <c>context.UserName</c> into <c>session.LastModifiedBy</c> eagerly, so it read a null and the later
/// assignment to the context changed nothing.
/// </item>
/// </list>
///
/// The fix relays the user from inside <c>CreateMessageContextWithMaybeTenantFrame</c>, which is itself a
/// hoisted variable-source frame that every session frame depends on (<c>OpenMartenSessionFrame</c> resolves
/// the <c>MessageContext</c>), so the sort GUARANTEES the relay precedes every session open rather than
/// merely happening to.
/// </summary>
public class user_name_relay_into_marten_session : IntegrationContext
{
    private const string TheUserName = "auditor@example.com";

    public user_name_relay_into_marten_session(AppFixture fixture) : base(fixture)
    {
    }

    private static ClaimsPrincipal authenticatedUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, TheUserName)], "TestAuth"));
    }

    private Task<IScenarioResult> warmTheRouteAsync(string method, string url)
    {
        return Scenario(x =>
        {
            if (method == "POST")
            {
                x.Post.Json(new RecordUserName(Guid.NewGuid())).ToUrl(url);
                x.StatusCodeShouldBe(204);
            }
            else
            {
                x.Get.Url(url);
            }
        });
    }

    [Fact]
    public async Task relays_the_user_name_to_a_session_only_chain()
    {
        // Cause 1. Nothing in this chain's signature is an IMessageContext or IMessageBus.
        var result = await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = authenticatedUser());
            x.Get.Url("/user/name/marten/session");
        });

        (await result.ReadAsTextAsync()).ShouldBe(TheUserName);
    }

    [Fact]
    public async Task relays_the_user_name_to_a_chain_with_both_a_bus_and_a_session()
    {
        // Cause 2. The old policy DID fire here -- and the session still saw a null, because the session
        // open was hoisted ahead of the inserted middleware frame.
        var result = await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = authenticatedUser());
            x.Get.Url("/user/name/marten/session-and-context");
        });

        (await result.ReadAsTextAsync()).ShouldBe($"{TheUserName}|{TheUserName}");
    }

    [Fact]
    public async Task relays_the_user_name_to_the_session_behind_an_imartenop_return()
    {
        // The shape from the issue: a pure function returning an IMartenOp. The side effect writes down
        // what the session it was handed knew, so this survives a round trip through Postgres.
        var id = Guid.NewGuid();

        await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = authenticatedUser());
            x.Post.Json(new RecordUserName(id)).ToUrl("/user/name/marten/op");
            x.StatusCodeShouldBe(204);
        });

        var result = await Scenario(x => x.Get.Url($"/user/name/marten/op/{id}"));

        (await result.ReadAsTextAsync()).ShouldBe(TheUserName);
    }

    [Fact]
    public async Task an_unauthenticated_request_leaves_the_session_alone()
    {
        var result = await Scenario(x => x.Get.Url("/user/name/marten/session"));

        (await result.ReadAsTextAsync()).ShouldBe("NONE");
    }

    [Theory]
    [InlineData("GET", "/user/name/marten/session")]
    [InlineData("GET", "/user/name/marten/session-and-context")]
    [InlineData("POST", "/user/name/marten/op")]
    public async Task the_user_name_relay_is_emitted_before_the_session_is_opened(string method, string url)
    {
        // This is the assertion that pins cause 2 permanently. The behavioural tests above would pass
        // again on a regression that only moved the relay back behind the session open on SOME other chain
        // shape, and the ordering is a property of JasperFx's frame arranger rather than of anything
        // visible in chain.Middleware -- so it has to be read off the generated source.
        //
        // Warm the route with a real request rather than calling InitializeSynchronously() from here.
        // Driving a second, independently compiled assembly out of the test leaves the chain holding a
        // _generatedType whose handler type cannot be resolved, and every later request to that route then
        // answers a 500 -- so a test written that way breaks its own sibling tests depending on the order
        // xUnit happens to pick.
        await warmTheRouteAsync(method, url);

        var chain = HttpChains.ChainFor(method, url);
        chain.ShouldNotBeNull();

        var source = chain.SourceCode;
        source.ShouldNotBeNull("Failed to generate the source code");

        const string relay = "Wolverine.Http.Runtime.UserNameMiddleware.Apply(httpContext, messageContext);";
        var relayIndex = source.IndexOf(relay, StringComparison.Ordinal);
        relayIndex.ShouldBeGreaterThanOrEqualTo(0,
            $"Expected the generated source to relay the user name by calling `{relay}`. " +
            $"Inlining the assignment instead would silently drop the OpenTelemetry `enduser.id` tag. " +
            $"Source code follows:\n{source}");

        var openSessionIndex = source.IndexOf("OpenSession(", StringComparison.Ordinal);
        openSessionIndex.ShouldBeGreaterThanOrEqualTo(0,
            $"Expected the generated source to open a Marten session. Source code follows:\n{source}");

        relayIndex.ShouldBeLessThan(openSessionIndex,
            "The user name relay must be emitted BEFORE the store session is opened -- " +
            "OutboxedSessionFactory.OpenSession copies context.UserName into session.LastModifiedBy eagerly, " +
            $"so a relay that runs afterwards has no effect at all. Source code follows:\n{source}");
    }
}
