using System.Linq;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

public class MimeTypeListTester
{
    [Fact]
    public void accepts_any()
    {
        // If empty, going to say it's true
        ShouldBeBooleanExtensions.ShouldBeTrue(new MimeTypeList().AcceptsAny());

        ShouldBeBooleanExtensions.ShouldBeTrue(new MimeTypeList("*/*").AcceptsAny());
        ShouldBeBooleanExtensions.ShouldBeTrue(new MimeTypeList("application/json,*/*").AcceptsAny());
        ShouldBeBooleanExtensions.ShouldBeFalse(new MimeTypeList("application/json,text/html").AcceptsAny());
    }

    [Fact]
    public void build_from_string()
    {
        var list = new MimeTypeList("text/json");
        list.ShouldHaveTheSameElementsAs("text/json");
    }

    [Fact]
    public void build_with_complex_mimetypes()
    {
        var list =
            new MimeTypeList(
                "application/xml,application/xhtml+xml,text/html;q=0.9, text/plain;q=0.8,image/png,*/*;q=0.5");

        SpecificationExtensions.ShouldHaveTheSameElementsAs<string>(list, "application/xml", "application/xhtml+xml",
            "text/html", "text/plain",
            "image/png", "*/*");
    }

    [Fact]
    public void build_with_multiple_mimetypes()
    {
        var list = new MimeTypeList("text/json,application/json");
        SpecificationExtensions.ShouldHaveTheSameElementsAs<string>(list, "text/json",
            EnvelopeConstants.JsonContentType);
    }

    [Fact]
    public void matches_negative()
    {
        var list = new MimeTypeList("text/json,application/json");
        ShouldBeBooleanExtensions.ShouldBeFalse(list.Matches("weird"));
        ShouldBeBooleanExtensions.ShouldBeFalse(list.Matches("weird", "wrong"));
    }

    [Fact]
    public void matches_positive()
    {
        var list = new MimeTypeList("text/json,application/json");
        ShouldBeBooleanExtensions.ShouldBeTrue(list.Matches("text/json"));
        ShouldBeBooleanExtensions.ShouldBeTrue(list.Matches(EnvelopeConstants.JsonContentType));
        ShouldBeBooleanExtensions.ShouldBeTrue(list.Matches("text/json", EnvelopeConstants.JsonContentType));
    }

    [Fact]
    public void should_ignore_empty_string()
    {
        var list = new MimeTypeList(string.Empty);
        Enumerable.Count<string>(list).ShouldBe(0);
    }

    [Fact]
    public void should_ignore_whitespace_only_string()
    {
        var list = new MimeTypeList("    ");
        Enumerable.Count<string>(list).ShouldBe(0);
    }
}
