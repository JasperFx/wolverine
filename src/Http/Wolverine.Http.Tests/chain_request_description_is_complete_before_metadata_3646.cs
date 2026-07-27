using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using ServiceContainer = JasperFx.ServiceContainer;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-3646: <c>HttpChain.applyMetadata()</c> turns <c>IsFormData</c> / <c>RequestType</c> /
///     <c>RequestBodyIsOptional</c> into <c>Accepts</c> metadata and the API description, and it is the LAST
///     statement of the <see cref="HttpChain" /> constructor. Everything it reads has to be assigned by then.
///
///     <para>The originally suspected cause — codegen — was wrong, and these first tests are what disproved it.
///     The parameter strategies (including <c>AsParamatersAttributeUsage.TryMatch</c> and the
///     <c>AsParametersBindingFrame</c> it builds) run during construction, not during compilation, so a chain
///     from <c>HttpChain.ChainFor</c> — which compiles nothing — is already described correctly.</para>
///
///     <para>What WAS broken is the construction path: the route reaches an attribute-routed chain from inside
///     the constructor, but <c>HttpGraph.Add</c> used to map it onto the finished chain afterwards. Since
///     <c>MapToRoute()</c> is what runs the parameter strategies, metadata on those chains was built from a null
///     <c>RequestType</c> and an empty method list. <c>Add</c> now routes through the constructor.</para>
///
///     <para>The ordering was never stated anywhere, which is why the gap survived. These tests state it. If
///     someone moves strategy execution later, the description silently reverts to defaults — a query-only GET
///     advertises a form body again (GH-3630), a <c>[FromBody]</c> member is described as its containing type
///     (GH-3135), a published message advertises no body at all — and these fail instead.</para>
/// </summary>
public class chain_request_description_is_complete_before_metadata_3646
{
    [Fact]
    public void a_form_bound_asparameters_type_is_described_without_compiling()
    {
        var chain = HttpChain.ChainFor<Gh3646FormEndpoint>(x => x.Post(null!));

        chain.IsFormData.ShouldBeTrue();
        chain.RequestType.ShouldBe(typeof(Gh3646FormCommand));
    }

    [Fact]
    public void a_query_only_asparameters_type_is_described_without_compiling()
    {
        // GH-3630: this is what stops a GET advertising a form body and being dropped from route matching.
        var chain = HttpChain.ChainFor<Gh3646QueryEndpoint>(x => x.Get(null!));

        chain.IsFormData.ShouldBeFalse();
    }

    [Fact]
    public void a_from_body_member_narrows_the_request_type_without_compiling()
    {
        // GH-3135: the body is the PAYLOAD member's type, never the [AsParameters] container.
        var chain = HttpChain.ChainFor<Gh3646BodyEndpoint>(x => x.Post(null!));

        chain.RequestType.ShouldBe(typeof(Gh3646Payload));
        chain.AsParametersType.ShouldBe(typeof(Gh3646BodyCommand));
    }

    [Fact]
    public void a_nullable_from_body_member_is_described_as_optional_without_compiling()
    {
        // GH-3135 WS2/WS3: nullability drives requestBody.required.
        HttpChain.ChainFor<Gh3646BodyEndpoint>(x => x.Post(null!))
            .RequestBodyIsOptional.ShouldBeTrue();
    }

    /// <summary>
    ///     The invariant stated end-to-end: the metadata built by the constructor already reflects the
    ///     description, so nothing downstream depends on compilation having happened.
    /// </summary>
    [Fact]
    public void the_metadata_built_by_the_constructor_already_reflects_the_description()
    {
        var query = HttpChain.ChainFor<Gh3646QueryEndpoint>(x => x.Get(null!));
        var endpoint = query.BuildEndpoint(RouteWarmup.Lazy);

        endpoint.Metadata.OfType<IAcceptsMetadata>().ShouldBeEmpty(
            "a query-only GET reads no request body, so it must advertise none even when nothing has compiled");

        var form = HttpChain.ChainFor<Gh3646FormEndpoint>(x => x.Post(null!));
        form.BuildEndpoint(RouteWarmup.Lazy).Metadata.OfType<IAcceptsMetadata>()
            .SelectMany(x => x.ContentTypes)
            .ShouldContain("multipart/form-data");
    }

    /// <summary>
    ///     The case the tests above did NOT cover, and the one that was actually broken: a chain whose route is
    ///     supplied explicitly instead of by a <c>[WolverineHttpMethod]</c> attribute. <c>HttpGraph.Add</c> used to
    ///     construct the chain and only then call <c>MapToRoute()</c> — which is what runs the parameter
    ///     strategies — so <c>applyMetadata()</c>, the constructor's last statement, had already snapshotted a
    ///     null <c>RequestType</c> and an empty method list. <c>PublishMessage&lt;T&gt;</c> and
    ///     <c>SendMessage&lt;T&gt;</c> both build their endpoints this way.
    /// </summary>
    [Fact]
    public void an_explicitly_routed_chain_is_described_the_same_as_an_attribute_routed_one()
    {
        var chain = buildGraph().Add(new MethodCall(typeof(Gh3646ExplicitlyRoutedEndpoint),
            nameof(Gh3646ExplicitlyRoutedEndpoint.Handle)), System.Net.Http.HttpMethod.Post, "/3646/explicit");

        chain.RequestType.ShouldBe(typeof(Gh3646Payload));

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var accepts = endpoint.Metadata.OfType<IAcceptsMetadata>().ShouldHaveSingleItem();
        accepts.RequestType.ShouldBe(typeof(Gh3646Payload));
        accepts.ContentTypes.ShouldContain("application/json");

        endpoint.Metadata.OfType<HttpMethodMetadata>()
            .SelectMany(x => x.HttpMethods)
            .ShouldContain("POST");
    }

    // Mirrors what HttpChain.ChainFor() builds internally, since HttpGraph.Add is what is under test here.
    private static HttpGraph buildGraph()
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        return new HttpGraph(new WolverineOptions(), container);
    }
}

public class Gh3646ExplicitlyRoutedEndpoint
{
    [WolverineIgnore]
    public string Handle(Gh3646Payload payload) => "ok";
}

public record Gh3646Payload(string Name);

public record Gh3646FormCommand([FromForm] string Name);

public record Gh3646QueryCommand([FromQuery] string? Name);

public record Gh3646BodyCommand([FromRoute] Guid Id, [FromBody] Gh3646Payload? Body);

public class Gh3646FormEndpoint
{
    [WolverineIgnore]
    [WolverinePost("/3646/form")]
    public string Post([AsParameters] Gh3646FormCommand command) => "ok";
}

public class Gh3646QueryEndpoint
{
    [WolverineIgnore]
    [WolverineGet("/3646/query")]
    public string Get([AsParameters] Gh3646QueryCommand command) => "ok";
}

public class Gh3646BodyEndpoint
{
    [WolverineIgnore]
    [WolverinePost("/3646/body/{id:guid}")]
    public string Post([AsParameters] Gh3646BodyCommand command) => "ok";
}
