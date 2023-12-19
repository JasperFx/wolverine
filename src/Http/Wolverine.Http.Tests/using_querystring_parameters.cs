using Shouldly;

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

        body.ReadAsText().ShouldBe("Age is 8");
    }
    
    [Fact]
    public async Task use_parsed_enum_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=north");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }

    [Fact]
    public async Task use_explicitly_mapped_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/explicit?name=north");
        });

        body.ReadAsText().ShouldBe("north");
    }
    
    [Fact]
    public async Task use_explicitly_mapped_querystring_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/explicit");
        });

        body.ReadAsText().ShouldBeEmpty();
    }
    
        
    [Fact]
    public async Task use_parsed_enum_querystring_hit_2()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=North");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }

    [Fact]
    public async Task use_parsed_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=11");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 11");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
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

        body.ReadAsText().ShouldBe("Name is Magic");
    }

    [Fact]
    public async Task use_string_querystring_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is missing");
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

        body.ReadAsText().ShouldBe("Amount is 42.1");
    }

    #endregion
}