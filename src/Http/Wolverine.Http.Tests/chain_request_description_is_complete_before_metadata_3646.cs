using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Metadata;
using Shouldly;
using Wolverine.Attributes;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-3646, resolved as an INVARIANT rather than a fix.
///
///     <para>The concern was that <c>HttpChain.applyMetadata()</c> — which turns <c>IsFormData</c> /
///     <c>RequestType</c> / <c>RequestBodyIsOptional</c> into <c>Accepts</c> metadata and the API description —
///     might observe those values before the codegen frames that assign them had run, making the description a
///     race whose winner differs between hosts.</para>
///
///     <para>It does not. <c>applyMetadata()</c> is the LAST statement of the <see cref="HttpChain" />
///     constructor, and the endpoint's parameter strategies (including
///     <c>AsParamatersAttributeUsage.TryMatch</c> and the <c>AsParametersBindingFrame</c> it builds) have
///     already run by then — during construction, not during compilation. <c>HttpChain.ChainFor</c> compiles
///     nothing, so the assertions below prove the description is complete with no codegen at all.</para>
///
///     <para>That ordering is currently accidental: nothing stated it, and nothing would catch a refactor that
///     deferred parameter matching to compile time. These tests state it. If someone moves strategy execution
///     later, the description silently reverts to defaults — a query-only GET would advertise a form body again
///     (GH-3630) and a <c>[FromBody]</c> member would be described as its containing type (GH-3135) — and these
///     fail instead.</para>
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
