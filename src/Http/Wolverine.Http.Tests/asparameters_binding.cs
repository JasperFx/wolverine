using System.Net;
using System.Text;
using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;
using WolverineWebApi.Forms;

namespace Wolverine.Http.Tests;

public class asparameters_binding : IntegrationContext
{
    public asparameters_binding(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task fill_all_fields()
    {
        #region sample_using_asparameters_test
        var result = await Host.Scenario(x => x
            .Post
            .FormData(new Dictionary<string, string>
            {
                { "EnumFromForm", "east" },
                { "StringFromForm", "string2" },
                { "IntegerFromForm", "2" },
                { "FloatFromForm", "2.2" },
                { "BooleanFromForm", "true" },
                { "StringNotUsed", "string3" }
            }).QueryString("EnumFromQuery", "west")
            .QueryString("StringFromQuery", "string1")
            .QueryString("IntegerFromQuery", "1")
            .QueryString("FloatFromQuery", "1.1")
            .QueryString("BooleanFromQuery", "true")
            .QueryString("IntegerNotUsed", "3")
            .ToUrl("/api/asparameters1")
        );
        var response = await result.ReadAsJsonAsync<AsParametersQuery>();
        response.EnumFromForm.ShouldBe(Direction.East);
        response.StringFromForm.ShouldBe("string2");
        response.IntegerFromForm.ShouldBe(2);
        response.FloatFromForm.ShouldBe(2.2f);
        response.BooleanFromForm.ShouldBeTrue();
        response.EnumFromQuery.ShouldBe(Direction.West);
        response.StringFromQuery.ShouldBe("string1");
        response.IntegerFromQuery.ShouldBe(1);
        response.FloatFromQuery.ShouldBe(1.1f);
        response.BooleanFromQuery.ShouldBeTrue();
        response.EnumNotUsed.ShouldBe(default);
        response.StringNotUsed.ShouldBe(default);
        response.IntegerNotUsed.ShouldBe(default);
        response.FloatNotUsed.ShouldBe(default);
        response.BooleanNotUsed.ShouldBe(default);

        #endregion
    }

    [Fact]
    public async Task headers_miss()
    {
        var result = await Host.Scenario(x => x
            .Post
            .FormData(new Dictionary<string, string>
            {
                { "EnumFromForm", "east" },
                { "StringFromForm", "string2" },
                { "IntegerFromForm", "2" },
                { "FloatFromForm", "2.2" },
                { "BooleanFromForm", "true" },
                { "StringNotUsed", "string3" }
            }).QueryString("EnumFromQuery", "west")
            .QueryString("StringFromQuery", "string1")
            .QueryString("IntegerFromQuery", "1")
            .QueryString("FloatFromQuery", "1.1")
            .QueryString("BooleanFromQuery", "true")
            .QueryString("IntegerNotUsed", "3")
            .ToUrl("/api/asparameters1")
        );
        var response = await result.ReadAsJsonAsync<AsParametersQuery>();
        response.StringHeader.ShouldBeNull();
        response.NumberHeader.ShouldBe(5);
        response.NullableHeader.ShouldBeNull();
    }

    [Fact]
    public async Task headers_hit()
    {
        var result = await Host.Scenario(x =>
            {
                x.WithRequestHeader("x-string", "Red");
                x.WithRequestHeader("x-number", "303");
                x.WithRequestHeader("x-nullable-number", "13");

                x
                    .Post
                    .FormData(new Dictionary<string, string>
                    {
                        { "EnumFromForm", "east" },
                        { "StringFromForm", "string2" },
                        { "IntegerFromForm", "2" },
                        { "FloatFromForm", "2.2" },
                        { "BooleanFromForm", "true" },
                        { "StringNotUsed", "string3" }
                    }).QueryString("EnumFromQuery", "west")
                    .QueryString("StringFromQuery", "string1")
                    .QueryString("IntegerFromQuery", "1")
                    .QueryString("FloatFromQuery", "1.1")
                    .QueryString("BooleanFromQuery", "true")
                    .QueryString("IntegerNotUsed", "3")
                    .ToUrl("/api/asparameters1");
            }
        );
        var response = await result.ReadAsJsonAsync<AsParametersQuery>();
        response.StringHeader.ShouldBe("Red");
        response.NumberHeader.ShouldBe(303);
        response.NullableHeader.ShouldBe(13);
    }

    [Fact]
    public async Task post_body_services_and_route_arguments()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new AsParameterBody { Name = "Jeremy", Direction = Direction.East, Distance = 133 })
                .ToUrl("/asp2/croaker/42");

            // x.Post.Url("/asp2/croaker/42");
        });

        var response = await result.ReadAsJsonAsync<AsParametersQuery2>();

        // Routes
        response.Id.ShouldBe("croaker");
        response.Number.ShouldBe(42);

        // Body

        // First check this for OpenAPI generation
        var options = Host.Services.GetRequiredService<WolverineHttpOptions>();
        var chain = options.Endpoints!.ChainFor("POST", "/asp2/{id}/{number}");
        chain!.RequestType.ShouldBe(typeof(AsParameterBody));

        response.Body.Name.ShouldBe("Jeremy");
        response.Body.Direction.ShouldBe(Direction.East);
        response.Body.Distance.ShouldBe(133);
    }

    [Fact]
    public async Task use_record_for_as_parameters()
    {
        var result = await Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { { "test", "true" } })
                .QueryString("Number", "2")
                .ToUrl("/asparameterrecord/idvalue");

            x.WithRequestHeader("x-direction", "East");
        });

        var value = await result.ReadAsJsonAsync<AsParameterRecord>();
        value.Id.ShouldBe("idvalue");
        value.Number.ShouldBe(2);
        value.Direction.ShouldBe(Direction.East);
        value.IsTrue.ShouldBeTrue();
    }

    [Fact]
    public async Task using_with_FluentValidation_middleware()
    {
        // Happy path
        await Scenario(x =>
        {
            x.Get.Url("/asparameters/validated")
                .QueryString("Name", "Jeremy")
                .QueryString("Age", "51");
        });

        var result = await Scenario(x =>
        {
            x.Get.Url("/asparameters/validated")
                .QueryString("Age", "51");

            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task using_FluentValidation_with_AsParameters_and_FromBody_happy_path()
    {
        await Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.Forms.ValidatedWithFromBody.ValidatedQueryBody { HasDog = true, HasCat = false })
                .ToUrl("/asparameters/validated_with_from_body")
                .QueryString("Name", "Jeremy")
                .QueryString("Age", "51");
        });
    }

    [Fact]
    public async Task using_FluentValidation_with_AsParameters_and_FromBody_should_validate_asparameters_type()
    {
        // Missing Name (required by the ValidatedWithFromBody validator)
        await Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.Forms.ValidatedWithFromBody.ValidatedQueryBody { HasDog = true, HasCat = false })
                .ToUrl("/asparameters/validated_with_from_body")
                .QueryString("Age", "51");

            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task using_FluentValidation_with_AsParameters_and_FromBody_missing_body_should_fail()
    {
        // No body at all (required by the ValidatedWithFromBody validator)
        await Scenario(x =>
        {
            x.Post.Url("/asparameters/validated_with_from_body")
                .QueryString("Name", "Jeremy")
                .QueryString("Age", "51");

            x.StatusCodeShouldBe(400);
        });
    }

    // GH-3135 WS3: a nullable [FromBody] member is optional — a request with no body binds null and
    // the endpoint runs (200) instead of returning 400 ("input does not contain any JSON tokens").
    // "No body" is decided the way minimal APIs decide it (GH-4935): Content-Length: 0, or the server
    // saying there cannot be one. An Alba scenario always says there CAN be one, so the no-body request
    // is sent with an explicit Content-Length: 0, which is what HttpClient sends for a bodiless POST.
    [Fact]
    public async Task nullable_from_body_missing_binds_null()
    {
        var result = await Scenario(x =>
        {
            x.Post.Url("/api/3135/optional-body?Name=Jeremy");
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("no-body");
    }

    // GH-4935, question 1, decided as minimal API parity: when the server says the request CAN have a
    // body and there is nothing in it, that is not "no body", it is an empty body -- invalid JSON, 400.
    // Alba always reports CanHaveBody, so a scenario that sends nothing at all lands here, exactly as
    // the same scenario against a minimal API endpoint does.
    [Fact]
    public async Task nullable_from_body_with_an_empty_body_the_server_cannot_rule_out_is_a_400()
    {
        await Scenario(x =>
        {
            x.Post.Url("/api/3135/optional-body?Name=Jeremy");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task nullable_from_body_present_binds_value()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.AddPassengerPayload("Bob"))
                .ToUrl("/api/3135/optional-body")
                .QueryString("Name", "Jeremy");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("body:Bob");
    }

    // A chunked request carries no Content-Length, so an optional body sent chunked must still bind
    // instead of being mistaken for a missing body. Sent through the real TestServer client handler so
    // the server sees genuine chunked framing (CanHaveBody = true, ContentLength = null).
    [Fact]
    public async Task nullable_from_body_sent_chunked_binds_value()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/3135/optional-body?Name=Jeremy")
        {
            Content = new StringContent("{\"passengerName\":\"Bob\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await Host.Server.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("body:Bob");
    }

    // A chunked request with zero bytes is an EMPTY body, not a missing one: the server says it can have
    // a body (CanHaveBody is true), so it is read, and there is no JSON in it. 400, as minimal APIs
    // answer it. GH-4935, question 1.
    [Fact]
    public async Task nullable_from_body_sent_chunked_but_empty_is_a_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/3135/optional-body?Name=Jeremy")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await Host.Server.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // GH-4935, question 2, decided as minimal API parity: a top-level NULLABLE body parameter is an
    // optional body too, not only a nullable [FromBody] member of an [AsParameters] type.
    [Fact]
    public async Task nullable_body_parameter_missing_binds_null()
    {
        var result = await Scenario(x =>
        {
            x.Post.Url("/api/4935/optional-body-param");
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("no-body");
    }

    [Fact]
    public async Task nullable_body_parameter_present_binds_value()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.AddPassengerPayload("Bob")).ToUrl("/api/4935/optional-body-param");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("body:Bob");
    }

    [Fact]
    public async Task nullable_body_parameter_sent_chunked_binds_value()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/4935/optional-body-param")
        {
            Content = new StringContent("{\"passengerName\":\"Bob\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await Host.Server.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("body:Bob");
    }

    // Regression guard for question 2: a NON-nullable body parameter is still required.
    [Fact]
    public async Task non_nullable_body_parameter_missing_still_fails()
    {
        await Scenario(x =>
        {
            x.Post.Url("/api/4935/required-body-param");
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(400);
        });
    }

    // Regression guard: a NON-nullable [FromBody] member is still required — a missing body 400s.
    [Fact]
    public async Task non_nullable_from_body_missing_still_fails()
    {
        await Scenario(x =>
        {
            x.Post.Url($"/api/3135/journey/{Guid.NewGuid()}/passenger");
            x.StatusCodeShouldBe(400);
        });
    }
}