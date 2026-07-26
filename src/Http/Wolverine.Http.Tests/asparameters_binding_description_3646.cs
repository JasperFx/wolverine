using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-3646. <c>HttpChain.applyMetadata()</c> and <c>fillRequestType()</c> run during endpoint BUILD, which
///     can precede chain compilation depending on <c>WolverineHttpOptions.WarmUpRoutes</c>. Anything they read
///     that is only assigned while a codegen frame is constructed is therefore a race whose winner differs
///     between hosts — GH-3630 was the reproducible instance, where the test host compiled first and a real
///     application did not.
///
///     <para>The remaining instances were <c>RequestType</c> (narrowed from the <c>[AsParameters]</c> container
///     to the <c>[FromBody]</c> member's type) and <c>RequestBodyIsOptional</c> (the member's nullability, which
///     drives <c>requestBody.required</c> — GH-3135). Both were assigned only inside
///     <c>AsParametersBindingFrame</c>'s constructor.</para>
///
///     <para>These tests pin the description as available WITHOUT compiling anything: they call the static
///     describe seam directly, which is what the parameter strategy now uses to assign both values up front.</para>
/// </summary>
public class asparameters_binding_description_3646
{
    private static (bool HasForm, Type? BodyType, bool BodyIsOptional) describe<T>()
    {
        AsParamatersAttributeUsage.DescribeAsParametersBinding(typeof(T), out var hasForm, out var bodyType,
            out var bodyIsOptional);
        return (hasForm, bodyType, bodyIsOptional);
    }

    [Fact]
    public void describes_a_from_body_constructor_member_without_compiling_the_chain()
    {
        // The GH-3135 shape: a typed [FromRoute] id split from a [FromBody] payload. The body is the PAYLOAD
        // member's type, never the [AsParameters] container.
        var described = describe<Gh3646SplitCommand>();

        described.HasForm.ShouldBeFalse();
        described.BodyType.ShouldBe(typeof(Gh3646Payload));
        described.BodyIsOptional.ShouldBeFalse("a non-nullable [FromBody] member is a required body");
    }

    [Fact]
    public void a_nullable_from_body_member_is_an_optional_body()
    {
        // GH-3135 WS2/WS3: nullability drives requestBody.required, and losing it was the second half of the
        // ordering hazard.
        describe<Gh3646OptionalBodyCommand>().BodyIsOptional.ShouldBeTrue();
    }

    [Fact]
    public void describes_a_from_body_property_member()
    {
        var described = describe<Gh3646PropertyBodyCommand>();

        described.BodyType.ShouldBe(typeof(Gh3646Payload));
    }

    [Fact]
    public void a_form_bound_type_reports_no_body_type()
    {
        var described = describe<Gh3646FormCommand>();

        described.HasForm.ShouldBeTrue();
        described.BodyType.ShouldBeNull();
    }

    [Fact]
    public void a_query_only_type_reports_neither()
    {
        // The GH-3630 case: reads nothing from the body at all, so the chain must advertise no request body.
        var described = describe<Gh3646QueryCommand>();

        described.HasForm.ShouldBeFalse();
        described.BodyType.ShouldBeNull();
    }

    [Fact]
    public void the_chain_carries_the_narrowed_request_type()
    {
        // End of the seam: what the strategy assigns from the description above is what the metadata and the
        // API description read.
        var chain = HttpChain.ChainFor<Gh3646Endpoint>(x => x.Post(null!));

        chain.RequestType.ShouldBe(typeof(Gh3646Payload));
        chain.AsParametersType.ShouldBe(typeof(Gh3646SplitCommand));
        chain.RequestBodyIsOptional.ShouldBeFalse();
    }
}

public record Gh3646Payload(string PassengerName);

public record Gh3646SplitCommand([FromRoute] Guid JourneyId, [FromBody] Gh3646Payload Body);

public record Gh3646OptionalBodyCommand([FromRoute] Guid JourneyId, [FromBody] Gh3646Payload? Body);

public record Gh3646FormCommand([FromForm] string Name);

public record Gh3646QueryCommand([FromQuery] string? Name);

public class Gh3646PropertyBodyCommand
{
    [FromBody]
    public Gh3646Payload? Body { get; set; }
}

public class Gh3646Endpoint
{
    [Wolverine.Attributes.WolverineIgnore]
    [WolverinePost("/3646/journey/{journeyId:guid}/passenger")]
    public string Post([AsParameters] Gh3646SplitCommand command) => "ok";
}
