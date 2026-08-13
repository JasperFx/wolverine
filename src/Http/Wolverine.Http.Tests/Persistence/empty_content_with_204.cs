using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests.Persistence;

// See EmptyContentWith204Endpoints
public class empty_content_with_204 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "empty_content_204";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints();
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task entity_miss_returns_204_with_an_empty_body()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/entity/nonexistent");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task entity_hit_still_returns_the_document()
    {
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(new Todo2 { Id = "real-one", Name = "Kareem" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/entity/real-one");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsJsonAsync<Todo2>())!.Name.ShouldBe("Kareem");
    }

    [Fact]
    public async Task required_is_forced_true_on_a_get_so_the_handler_never_sees_a_null()
    {
        // Required = false on the attribute, but EmptyContentWith204 on a GET overrides it. Without that,
        // the endpoint body would dereference a null Todo2 and blow up with a 500.
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/entity-not-required/nonexistent");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task no_content_if_missing_covers_a_null_response_body()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/body/missing");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();

        await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/body/found");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task no_content_if_missing_covers_a_null_string_response_body()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/string/missing");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_null_string_resource_is_a_404_rather_than_a_500()
    {
        // Regression: HttpHandler.WriteString dereferenced the null for its ContentLength and threw a
        // NullReferenceException, so a string returning endpoint answered 500 where every other resource
        // type answered 404.
        await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/string-default/missing");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task class_level_attribute_applies_to_the_methods()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/class-level/missing");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task a_method_can_opt_back_out_of_a_class_level_attribute()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url("/no-content/class-level-opt-out/missing");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task the_default_is_unchanged()
    {
        // Nothing about this feature is on unless you ask for it
        await theHost.Scenario(x =>
        {
            x.Get.Url("/global-no-content/get/missing");
            x.StatusCodeShouldBe(404);
        });
    }
}

// See EmptyContentWith204Endpoints
public class global_empty_content_with_204 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "global_empty_content_204";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            // The application wide answer for a required entity that could not be loaded
            opts.EntityDefaults.OnMissing = OnMissing.EmptyContentWith204;
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();

            // ... and the application wide answer for a null response body
            app.MapWolverineEndpoints(opts =>
                opts.OnMissingResponseBody = OnMissingResponseBody.NoContent204);
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task global_entity_default_reaches_a_plain_entity_attribute()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url("/global-no-content/entity/nonexistent");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task global_response_body_default_reaches_a_get()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/global-no-content/get/missing");
            x.StatusCodeShouldBe(204);
        });

        (await result.ReadAsTextAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_endpoint_can_opt_back_out_of_the_global_default()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url("/global-no-content/opt-out/missing");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task the_global_default_does_not_reach_a_post()
    {
        // A 204 in place of a resource on a POST would turn a failed command into an apparent success,
        // so the application wide setting stops at the safe reads.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new CreateTodo2("missing", "Nope")).ToUrl("/global-no-content/post");
            x.StatusCodeShouldBe(404);
        });
    }
}

public class missing_response_body_metadata_and_validation
{
    [Fact]
    public void openapi_advertises_204_instead_of_404_when_opted_in()
    {
        var chain = HttpChain.ChainFor<EmptyContentWith204Endpoints>(x =>
            EmptyContentWith204Endpoints.GetBody(null!));

        var statuses = chain.BuildEndpoint(RouteWarmup.Lazy).Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ToArray();

        statuses.ShouldContain(200);
        statuses.ShouldContain(204);
        statuses.ShouldNotContain(404);
    }

    [Fact]
    public void openapi_still_advertises_404_by_default()
    {
        var chain = HttpChain.ChainFor<EmptyContentWith204Endpoints>(x =>
            EmptyContentWith204Endpoints.GetBodyDefault(null!));

        chain.BuildEndpoint(RouteWarmup.Lazy).Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ShouldContain(404);
    }

    [Fact]
    public void a_string_endpoint_advertises_204_only_when_it_opted_in()
    {
        // A string endpoint has never advertised a missing-resource status, so the default stays as it was
        // and only the opt-in adds one. Otherwise every string returning endpoint's OpenAPI would change.
        HttpChain.ChainFor<EmptyContentWith204Endpoints>(x => EmptyContentWith204Endpoints.GetStringDefault(null!))
            .BuildEndpoint(RouteWarmup.Lazy).Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ShouldBe([200]);

        HttpChain.ChainFor<EmptyContentWith204Endpoints>(x => EmptyContentWith204Endpoints.GetString(null!))
            .BuildEndpoint(RouteWarmup.Lazy).Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ShouldBe([200, 204]);
    }

    [Fact]
    public void throws_when_no_content_if_missing_is_used_on_a_post()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<PostWithNoContentIfMissing>(x => x.Post(null!)));

        ex.Message.ShouldContain("POST");
        ex.Message.ShouldContain("GET and QUERY");
    }

    [Fact]
    public void throws_when_a_class_level_attribute_reaches_a_non_read_endpoint()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<ClassWithNoContentIfMissingAndADelete>(x => x.Delete(null!)));

        ex.Message.ShouldContain("Move it onto the individual GET/QUERY methods");
    }

    [Fact]
    public void a_method_level_opt_out_rescues_a_non_read_endpoint_in_a_decorated_class()
    {
        // [NotFoundIfMissing] is the documented escape hatch, so this must not throw
        Should.NotThrow(() =>
            HttpChain.ChainFor<ClassWithNoContentIfMissingAndADelete>(x => x.DeleteButOptedOut(null!)));
    }

    [Fact]
    public void throws_when_both_attributes_are_on_the_same_member()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<ContradictoryEndpoint>(x => x.Get(null!)));

        ex.Message.ShouldContain("mutually exclusive");
    }
}

public class PostWithNoContentIfMissing
{
    [Attributes.WolverineIgnore]
    [WolverinePost("/no-content/invalid-post"), NoContentIfMissing]
    public Todo2? Post(CreateTodo2 command) => null;
}

[NoContentIfMissing]
public class ClassWithNoContentIfMissingAndADelete
{
    [Attributes.WolverineIgnore]
    [WolverineDelete("/no-content/invalid-delete")]
    public Todo2? Delete(DeleteTodo command) => null;

    [Attributes.WolverineIgnore]
    [WolverineDelete("/no-content/rescued-delete"), NotFoundIfMissing]
    public Todo2? DeleteButOptedOut(DeleteTodo command) => null;
}

public class ContradictoryEndpoint
{
    [Attributes.WolverineIgnore]
    [WolverineGet("/no-content/contradictory/{id}"), NoContentIfMissing, NotFoundIfMissing]
    public Todo2? Get(string id) => null;
}
