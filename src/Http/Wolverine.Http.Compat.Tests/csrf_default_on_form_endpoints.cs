using System.Net;
using Alba;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     GH-5147: Wolverine form and file upload endpoints require antiforgery (CSRF) validation by default on
///     .NET 11, as Minimal API's do, and stay opt-in on .NET 10, where the same metadata makes ASP.NET Core throw
///     unless the application calls UseAntiforgery().
/// </summary>
public class csrf_default_on_form_endpoints : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private bool? requirement(string route) =>
        _host.Chain(route).Endpoint!.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation;

    [Fact]
    public void the_default_follows_the_target_framework()
    {
#if NET11_0_OR_GREATER
        WolverineHttpOptions.AutoAntiforgeryOnFormEndpointsByDefault.ShouldBeTrue();
#else
        WolverineHttpOptions.AutoAntiforgeryOnFormEndpointsByDefault.ShouldBeFalse();
#endif
    }

    [Fact]
    public void endpoint_metadata_by_default()
    {
#if NET11_0_OR_GREATER
        requirement("/compat/csrf/form").ShouldBe(true);
        requirement("/compat/csrf/file").ShouldBe(true);
#else
        requirement("/compat/csrf/form").ShouldBeNull();
        requirement("/compat/csrf/file").ShouldBeNull();
#endif
        requirement("/compat/csrf/json").ShouldBeNull();
        requirement("/compat/csrf/get").ShouldBeNull();
        requirement("/compat/csrf/json-required").ShouldBe(true);
        requirement("/compat/csrf/form-disabled").ShouldBe(false);
    }

    [Fact]
    public void generated_code_checks_the_antiforgery_result_only_where_validation_is_required()
    {
#if NET11_0_OR_GREATER
        _host.Chain("/compat/csrf/form").SourceCode!.ShouldContain("AntiforgeryValidation.Validate");
#endif
        _host.Chain("/compat/csrf/json-required").SourceCode!.ShouldContain("AntiforgeryValidation.Validate");
        _host.Chain("/compat/csrf/json").SourceCode!.ShouldNotContain("AntiforgeryValidation");
        _host.Chain("/compat/csrf/form-disabled").SourceCode!.ShouldNotContain("AntiforgeryValidation");
    }

    [Fact]
    public async Task a_same_origin_form_post_goes_through_without_UseAntiforgery()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form");
            x.WithRequestHeader("Sec-Fetch-Site", "same-origin");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("form:jeremy");
    }

    [Fact]
    public async Task a_non_browser_form_post_goes_through_without_UseAntiforgery()
    {
        // No Sec-Fetch-Site and no Origin: not a browser, so not a cross-site request forgery
        var result = await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("form:jeremy");
    }

    [Fact]
    public async Task a_cross_site_form_post()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form");
            crossSite(x);
#if NET11_0_OR_GREATER
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
#else
            // Opt-in on .NET 10: nothing protects the endpoint unless the application asks for it
            x.StatusCodeShouldBeOk();
#endif
        });

#if NET11_0_OR_GREATER
        (await result.ReadAsTextAsync()).ShouldContain("Antiforgery validation failed");
#endif
    }

    [Fact]
    public async Task a_cross_site_post_to_a_disabled_form_endpoint_goes_through()
    {
        await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form-disabled");
            crossSite(x);
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task a_cross_site_json_post_is_not_affected()
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new CompatDto("jeremy")).ToUrl("/compat/csrf/json");
            crossSite(x);
            x.StatusCodeShouldBeOk();
        });
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task a_cross_site_json_post_to_a_ValidateAntiforgery_endpoint_is_refused()
    {
        // Before GH-5147 this reached the handler: the CSRF middleware only records its verdict, and nothing
        // in a JSON endpoint reads the form, which is where ASP.NET Core would otherwise enforce it.
        await _host.Scenario(x =>
        {
            x.Post.Json(new CompatDto("jeremy")).ToUrl("/compat/csrf/json-required");
            crossSite(x);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }
#endif

    [Fact]
    public async Task gets_are_never_affected()
    {
        await _host.Scenario(x =>
        {
            x.Get.Url("/compat/csrf/get");
            crossSite(x);
            x.StatusCodeShouldBeOk();
        });
    }

    private static void crossSite(Scenario x)
    {
        x.WithRequestHeader("Sec-Fetch-Site", "cross-site");
        x.WithRequestHeader("Origin", "https://evil.example");
    }
}

public class csrf_default_can_be_changed
{
    [Fact]
    public async Task DisableAutoAntiforgeryOnFormEndpoints_turns_the_default_off()
    {
        await using var host = await CompatHost.For(opts => opts.DisableAutoAntiforgeryOnFormEndpoints());

        host.Chain("/compat/csrf/form").Endpoint!.Metadata.GetMetadata<IAntiforgeryMetadata>().ShouldBeNull();
        host.Chain("/compat/csrf/json-required").Endpoint!.Metadata.GetMetadata<IAntiforgeryMetadata>()!
            .RequiresValidation.ShouldBeTrue();

        await host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form");
            x.WithRequestHeader("Sec-Fetch-Site", "cross-site");
            x.WithRequestHeader("Origin", "https://evil.example");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task RequireAntiforgeryOnAll_still_lets_DisableAntiforgery_win()
    {
        // .NET 10 needs the middleware for any endpoint that requires validation
        await using var host = await CompatHost.For(
            opts => opts.RequireAntiforgeryOnAll(),
            services => services.AddAntiforgery(),
            app => app.UseAntiforgery());

        host.Chain("/compat/csrf/json").Endpoint!.Metadata.GetMetadata<IAntiforgeryMetadata>()!
            .RequiresValidation.ShouldBeTrue();
        host.Chain("/compat/csrf/form-disabled").Endpoint!.Metadata.GetMetadata<IAntiforgeryMetadata>()!
            .RequiresValidation.ShouldBeFalse();
    }

    [Fact]
    public async Task with_UseAntiforgery_a_form_post_without_a_token_is_refused_with_400()
    {
        await using var host = await CompatHost.For(
            opts => opts.AutoAntiforgeryOnFormEndpoints(),
            services => services.AddAntiforgery(),
            app => app.UseAntiforgery());

        // Was an unhandled InvalidOperationException from ReadFormAsync (a 500) before GH-5147
        var result = await host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" }).ToUrl("/compat/csrf/form");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });

        (await result.ReadAsTextAsync()).ShouldContain("Antiforgery validation failed");
    }
}
