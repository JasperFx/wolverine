using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using Shouldly;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_form_parameters : IntegrationContext
{
    public using_form_parameters(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_parsed_form_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>{
                    {"age","8"}
                })
                .ToUrl("/form/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 8");
    }

    [Fact]
    public async Task use_parsed_enum_form_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {direction = "north"})
                .ToUrl("/form/enum");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }

    [Fact]
    public async Task use_explicitly_mapped_form_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>{{"name", "north"}})
                .ToUrl("/form/explicit");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("north");
    }

    [Fact]
    public async Task use_explicitly_mapped_form_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {name = ""})
                .ToUrl("/form/explicit");
        });

        body.ReadAsText().ShouldBeEmpty();
    }

    [Fact]
    public async Task use_parsed_enum_form_hit_2()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {direction = "North"})
                .ToUrl("/form/enum");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }

    [Fact]
    public async Task use_parsed_form_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {})
                .ToUrl("/form/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_form_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {age = "garbage"})
                .ToUrl("/form/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_nullable_form_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>{{"age", "11"}})
                .ToUrl("/form/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 11");
    }

    [Fact]
    public async Task use_parsed_nullable_form_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {})
                .ToUrl("/form/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
    }

    [Fact]
    public async Task use_parsed_nullable_form_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {age = "garbage"})
                .ToUrl("/form/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
    }

    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task use_parsed_string_collection()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {collection = new[] {"foo", "bar", "baz"}})
                .ToUrl("/form/collection/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("foo,bar,baz");
    }

    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task use_parsed_int_collection()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {collection = new[] {5, 8, 13}})
                .ToUrl("/form/collection/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("5,8,13");
    }

    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task use_parsed_guid_collection()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var guid3 = Guid.NewGuid();

        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {collection = new[] {guid1, guid2, guid3}})
                .ToUrl("/form/collection/guid");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe($"{guid1},{guid2},{guid3}");
    }

    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task use_parsed_enum_collection()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {collection = new[] {Direction.North, Direction.East, Direction.South}})
                .ToUrl("/form/collection/enum");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe($"North,East,South");
    }

    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task using_string_array_completely_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post.FormData(new {values = new[] {"foo", "bar", "baz"}})
                .ToUrl("/form/stringarray");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("foo,bar,baz");
    }
    
    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task using_string_array_completely_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {})
                .ToUrl("/form/stringarray");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("none");
    }
    
    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task using_int_array_completely_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {values = new[] {4, 2, 1}})
                .ToUrl("/form/intarray");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("1,2,4");
    }
    
    [Fact(Skip = "Seems alba doesn't support collections in form data in any way")]
    public async Task using_int_array_completely_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {})
                .ToUrl("/form/intarray");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("none");
    }

    [Fact]
    public async Task using_datetime_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {})
                .ToUrl("/form/datetime");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("0001-01-01T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_with_invalid_value_parses_to_default()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new {value = "5-5-5"})
                .ToUrl("/form/datetime");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("0001-01-01T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_with_just_a_date()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string, string>{{"value", "2025-04-05"}})
                .ToUrl("/form/datetime");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05T00:00:00.0000000");
    }

    [Fact]
    public async Task using_datetime_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string,string>(){{"value", "2025-04-05T13:37:42.0123456"}})
                .ToUrl("/form/datetime");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05T13:37:42.0123456");
    }

    [Fact]
    public async Task using_datetime_with_from_query_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value", "2025-04-05T13:37:42.0123456"}})
                .ToUrl("/form/datetime2");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05T13:37:42.0123456");
    }
    
    [Fact]
    public async Task using_nullable_datetime_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData([])
                .ToUrl("/form/datetime/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Value is missing");
    }

    [Fact]
    public async Task using_nullable_datetime_with_just_a_date()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value","2025-04-05"}})
                .ToUrl("/form/datetime/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05T00:00:00.0000000");
    }

    [Fact]
    public async Task using_nullable_datetime_including_time()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value","2025-04-05T13:37:42.0123456"}})
                .ToUrl("/form/datetime/nullable");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05T13:37:42.0123456");
    }

    [Fact]
    public async Task using_dateonly_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData([])
                .ToUrl("/form/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("0001-01-01");
    }

    [Fact]
    public async Task using_dateonly_with_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string, string>(){{"value","2025-04-05"}})
                .ToUrl("/form/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05");
    }

    [Fact]
    public async Task using_dateonly_with_invalid_value_returns_default()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value","-2025"}})
                .ToUrl("/form/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("0001-01-01");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_default_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData([])
                .ToUrl("/form/dateonly/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Value is missing");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_value()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value","2025-04-05"}})
                .ToUrl("/form/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("2025-04-05");
    }

    [Fact]
    public async Task using_nullable_dateonly_with_invalid_value_returns_default()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>(){{"value","-2025"}})
                .ToUrl("/form/dateonly");

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("0001-01-01");
    }

    #region sample_form_value_usage

    [Fact]
    public async Task use_string_form_hit()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData(new Dictionary<string,string>{
                    ["name"] = "Magic"
                })
                .ToUrl("/form/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is Magic");
    }

    [Fact]
    public async Task use_string_form_miss()
    {
        var body = await Scenario(x =>
        {
            x.Post
                .FormData([])
                .ToUrl("/form/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is missing");
    }

    [Fact]
    public async Task use_decimal_form_hit()
     {
        var body = await Scenario(x =>
        {
            x.WithRequestHeader("Accept-Language", "fr-FR");
            x.Post
                .FormData(new Dictionary<string,string> (){
                    {"Amount", "42.1"}
                })
                .ToUrl("/form/decimal");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Amount is 42.1");
    }

    #endregion

    [Fact]
    public void trouble_shoot_form_matching()
    {
        var method = new MethodCall(typeof(FormEndpoints), "IntArray");
        var chain = new HttpChain(method, new HttpGraph(new WolverineOptions(), ServiceContainer.Empty()));

        var parameter = method.Method.GetParameters().Single();

        var variable = chain.TryFindOrCreateFormValue(parameter);
        variable.Creator.ShouldBeOfType<ParsedArrayFormValue>();
    }
}