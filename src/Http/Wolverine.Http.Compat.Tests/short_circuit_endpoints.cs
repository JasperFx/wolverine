#if NET11_0_OR_GREATER
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     GH-5147: ASP.NET Core 11's [ShortCircuit] on a Wolverine endpoint runs it inside the routing middleware,
///     and a short-circuited endpoint that would need antiforgery validation is refused at startup rather than
///     on every request.
/// </summary>
public class short_circuit_endpoints : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For(configureApp: app =>
        {
            app.UseRouting();

            // Anything between routing and the endpoint middleware: a short-circuited endpoint skips it
            app.Use(async (context, next) =>
            {
                context.Response.Headers["x-downstream"] = "ran";
                await next(context);
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public void the_attribute_reaches_the_endpoint_metadata()
    {
        _host.Chain("/compat/short-circuit").Endpoint!.Metadata.GetMetadata<IShortCircuitMetadata>().ShouldNotBeNull();
        _host.Chain("/compat/not-short-circuited").Endpoint!.Metadata.GetMetadata<IShortCircuitMetadata>().ShouldBeNull();
    }

    [Fact]
    public async Task a_short_circuited_endpoint_skips_the_rest_of_the_pipeline()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/compat/short-circuit");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("short-circuited");
        result.Context.Response.Headers.ContainsKey("x-downstream").ShouldBeFalse();

        var normal = await _host.Scenario(x =>
        {
            x.Get.Url("/compat/not-short-circuited");
            x.StatusCodeShouldBeOk();
        });

        normal.Context.Response.Headers["x-downstream"].ToString().ShouldBe("ran");
    }

    [Fact]
    public async Task a_short_circuited_form_endpoint_that_disabled_antiforgery_works()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" })
                .ToUrl("/compat/short-circuit/form-disabled");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("short-circuited:jeremy");
    }

    [Fact]
    public void a_short_circuited_form_endpoint_is_refused_at_startup()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<ShortCircuitFormEndpoints>(x => x.PostForm(null!)).BuildEndpoint(RouteWarmup.Lazy));

        ex.Message.ShouldContain("[ShortCircuit]");
        ex.Message.ShouldContain("form and file upload endpoints get by default");
        ex.Message.ShouldContain("[DisableAntiforgery]");
    }

    [Fact]
    public void a_short_circuited_ValidateAntiforgery_endpoint_is_refused_at_startup()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<ShortCircuitFormEndpoints>(x => x.PostJsonRequired(null!)).BuildEndpoint(RouteWarmup.Lazy));

        ex.Message.ShouldContain("[ShortCircuit]");
    }
}
#endif
