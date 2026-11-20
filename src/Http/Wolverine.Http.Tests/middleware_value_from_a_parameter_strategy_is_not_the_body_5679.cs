using System.Reflection;
using Alba;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Tests;

// GH-5679. A parameter strategy that installs middleware while it matches an endpoint parameter. The value the
// middleware's Before creates and its After and Finally take belongs to the server, so it must never be inferred
// as the JSON request body of a POST
public class middleware_value_from_a_parameter_strategy_is_not_the_body_5679 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the GH-5679 endpoints", type => type != typeof(LeasedNote5679Endpoints)));

                opts.AddParameterHandlingStrategy<LeasedNote5679Strategy>();
            });
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
    public async Task a_bodyless_post_reaches_the_endpoint_and_the_middleware_shares_its_lease()
    {
        NoteLease5679Middleware.Reset();

        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/notes5679/archive");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("archived");

        NoteLease5679Middleware.Opened.ShouldNotBeNull();
        NoteLease5679Middleware.Saved.ShouldBeSameAs(NoteLease5679Middleware.Opened);
        NoteLease5679Middleware.Released.ShouldBeSameAs(NoteLease5679Middleware.Opened);
    }

    [Fact]
    public async Task a_middleware_parameter_named_like_the_lease_variable_does_not_redeclare_it()
    {
        // Before creates a variable named noteLease5679, and After and Finally take a parameter of that same name.
        // Inferred as the body, the parameter was read into a second local of that name, which did not compile
        NamedNoteLease5679Middleware.Reset();

        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/notes5679/rename");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("renamed");

        NamedNoteLease5679Middleware.Opened.ShouldNotBeNull();
        NamedNoteLease5679Middleware.Saved.ShouldBeSameAs(NamedNoteLease5679Middleware.Opened);
        NamedNoteLease5679Middleware.Released.ShouldBeSameAs(NamedNoteLease5679Middleware.Opened);
    }

    [Theory]
    [InlineData("/notes5679/archive")]
    [InlineData("/notes5679/rename")]
    public async Task the_lease_is_not_read_from_the_request_body(string route)
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url(route);
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route);

        chain.RequestType.ShouldBe(typeof(void));
        chain.SourceCode.ShouldNotBeNull().ShouldNotContain("ReadJsonAsync");
    }
}

public class LeasedNote5679;

public class RenamedNote5679;

public class NoteLease5679;

public class NoteLease5679Middleware
{
    public static NoteLease5679? Opened;
    public static NoteLease5679? Saved;
    public static NoteLease5679? Released;

    public static void Reset()
    {
        Opened = Saved = Released = null;
    }

    public static NoteLease5679 Before()
    {
        return Opened = new NoteLease5679();
    }

    public static void After(NoteLease5679 lease)
    {
        Saved = lease;
    }

    public static void Finally(NoteLease5679 lease)
    {
        Released = lease;
    }
}

public class NamedNoteLease5679Middleware
{
    public static NoteLease5679? Opened;
    public static NoteLease5679? Saved;
    public static NoteLease5679? Released;

    public static void Reset()
    {
        Opened = Saved = Released = null;
    }

    public static NoteLease5679 Before()
    {
        return Opened = new NoteLease5679();
    }

    public static void After(NoteLease5679 noteLease5679)
    {
        Saved = noteLease5679;
    }

    public static void Finally(NoteLease5679 noteLease5679)
    {
        Released = noteLease5679;
    }
}

public static class LeasedNote5679Endpoints
{
    [WolverinePost("/notes5679/archive")]
    public static string Archive(LeasedNote5679 note) => "archived";

    [WolverinePost("/notes5679/rename")]
    public static string Rename(RenamedNote5679 note) => "renamed";
}

public class LeasedNote5679Strategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = null;

        var middlewareType = parameter.ParameterType == typeof(LeasedNote5679) ? typeof(NoteLease5679Middleware)
            : parameter.ParameterType == typeof(RenamedNote5679) ? typeof(NamedNoteLease5679Middleware)
            : null;

        if (middlewareType == null) return false;

        // Installed while the parameter is matched, before the chain has settled whether it has a request body
        new MiddlewareAttribute(middlewareType)
            .Modify(chain, container.GetInstance<WolverineOptions>().CodeGeneration, container);

        variable = new Variable(parameter.ParameterType, $"new {parameter.ParameterType.FullNameInCode()}()");
        return true;
    }
}
