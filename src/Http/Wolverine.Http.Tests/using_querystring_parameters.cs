using JasperFx;
using JasperFx.CodeGeneration.Frames;
using Shouldly;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_querystring_parameters : IntegrationContext
{
    public using_querystring_parameters(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_parsed_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int?age=8");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is 8");
    }

    [Fact]
    public async Task use_parsed_enum_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=north");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("North");
    }

    [Fact]
    public async Task use_explicitly_mapped_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/explicit?name=north");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("north");
    }

    [Fact]
    public async Task use_explicitly_mapped_querystring_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/explicit");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBeEmpty();
    }

    [Fact]
    public async Task use_parsed_enum_querystring_hit_2()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=North");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("North");
    }

    [Fact]
    public async Task use_parsed_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=11");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is 11");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is missing");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Age is missing");
    }

    [Fact]
    public async Task use_parsed_string_collection()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/collection/string")
                .QueryString("collection", "foo")
                .QueryString("collection", "bar")
                .QueryString("collection", "baz");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("foo,bar,baz");
    }

    [Fact]
    public async Task use_parsed_int_collection()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/collection/int")
                .QueryString("collection", "5")
                .QueryString("collection", "8")
                .QueryString("collection", "13");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("5,8,13");
    }

    [Fact]
    public async Task use_parsed_guid_collection()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var guid3 = Guid.NewGuid();

        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/collection/guid")
                .QueryString("collection", guid1.ToString())
                .QueryString("collection", guid2.ToString())
                .QueryString("collection", guid3.ToString());

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe($"{guid1},{guid2},{guid3}");
    }

    [Fact]
    public async Task use_parsed_enum_collection()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/collection/enum")
                .QueryString("collection", "North")
                .QueryString("collection", "East")
                .QueryString("collection", "South");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe($"North,East,South");
    }

    [Fact]
    public async Task using_string_array_completely_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/stringarray")
                .QueryString("values", "foo")
                .QueryString("values", "bar")
                .QueryString("values", "baz");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("foo,bar,baz");
    }
    
    [Fact]
    public async Task using_string_array_completely_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/stringarray");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("none");
    }
    
    [Fact]
    public async Task using_int_array_completely_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/intarray")
                .QueryString("values", "4")
                .QueryString("values", "2")
                .QueryString("values", "1");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("1,2,4");
    }
    
    [Fact]
    public async Task using_int_array_completely_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get
                .Url("/querystring/intarray");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("none");
    }

    [Fact]
    public async Task using_datetime_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime");
            
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("0001-01-01T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_with_invalid_value_parses_to_default()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime?value=-5-5-5");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("0001-01-01T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_with_just_a_date()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime?value=2025-04-05");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime?value=2025-04-05T13:37:42.0123456");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05T13:37:42.0123456");
    }

    [Fact]
    public async Task using_datetime_with_from_query_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime2?value=2025-04-05T13:37:42.0123456");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05T13:37:42.0123456");
    }
    
    [Fact]
    public async Task using_nullable_datetime_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime/nullable");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Value is missing");
    }

    [Fact]
    public async Task using_nullable_datetime_with_just_a_date()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime/nullable?value=2025-04-05");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05T00:00:00.0000000");
    }

    [Fact]
    public async Task using_nullable_datetime_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/datetime/nullable?value=2025-04-05T13:37:42.0123456");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05T13:37:42.0123456");
    }

    [Fact]
    public async Task using_dateonly_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("0001-01-01");
    }

    [Fact]
    public async Task using_dateonly_with_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly?value=2025-04-05");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05");
    }

    [Fact]
    public async Task using_dateonly_with_invalid_value_returns_default()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly?value=-2025");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("0001-01-01");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly/nullable");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Value is missing");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_value()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly?value=2025-04-05");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("2025-04-05");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_invalid_value_returns_default()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dateonly?value=-2025");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("0001-01-01");
    }

    #region sample_query_string_usage
    [Fact]
    public async Task use_string_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/string?name=Magic");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Name is Magic");
    }

    [Fact]
    public async Task use_string_querystring_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Name is missing");
    }

    [Fact]
    public async Task use_decimal_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.WithRequestHeader("Accept-Language", "fr-FR");
            x.Get.Url("/querystring/decimal?amount=42.1");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var text = await body.ReadAsTextAsync();
        text.ShouldBe("Amount is 42.1");
    }

    [Fact]
    public async Task use_decimal_fromquery_hit()
    {
        // GH-3586 follow-up: an explicit [FromQuery] on a scalar decimal used to throw at endpoint
        // discovery ("System.Decimal has multiple constructors"). It now binds from its own key.
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/decimal2?value=42.1");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("42.1");
    }

    [Fact]
    public async Task use_nullable_decimal_fromquery_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/decimal2/nullable?value=42.1");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("42.1");
    }

    [Fact]
    public async Task use_nullable_decimal_fromquery_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/decimal2/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("Value is missing");
    }

    [Fact]
    public async Task use_enum_fromquery_hit_with_string_name()
    {
        // Enum values arrive as their string name on the wire, not as an integer.
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum2?value=North");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("North");
    }

    [Fact]
    public async Task use_enum_fromquery_hit_with_lowercase_string_name()
    {
        // ...and the name parse is case-insensitive.
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum2?value=west");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("West");
    }

    [Fact]
    public async Task use_nullable_enum_fromquery_hit_with_string_name()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum2/nullable?value=South");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("South");
    }

    [Fact]
    public async Task use_nullable_enum_fromquery_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum2/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("Value is missing");
    }

    #endregion

    [Fact]
    public void trouble_shoot_querystring_matching()
    {
        var method = new MethodCall(typeof(QuerystringEndpoints), "IntArray");
        var chain = new HttpChain(method, new HttpGraph(new WolverineOptions(), ServiceContainer.Empty()));

        var parameter = method.Method.GetParameters().Single();

        var variable = chain.TryFindOrCreateQuerystringValue(parameter);
        variable!.Creator.ShouldBeOfType<ParsedArrayQueryStringValue>();
    }
}