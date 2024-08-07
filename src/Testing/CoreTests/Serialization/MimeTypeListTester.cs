using Wolverine.ComplianceTests;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

public class MimeTypeListTester
{
    [Fact]
    public void accepts_any()
    {
        // If empty, going to say it's true
        new MimeTypeList().AcceptsAny().ShouldBeTrue();

        new MimeTypeList("*/*").AcceptsAny().ShouldBeTrue();
        new MimeTypeList("application/json,*/*").AcceptsAny().ShouldBeTrue();
        new MimeTypeList("application/json,text/html").AcceptsAny().ShouldBeFalse();
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

        list.ShouldHaveTheSameElementsAs("application/xml", "application/xhtml+xml",
            "text/html", "text/plain",
            "image/png", "*/*");
    }

    [Fact]
    public void build_with_multiple_mimetypes()
    {
        var list = new MimeTypeList("text/json,application/json");
        list.ShouldHaveTheSameElementsAs("text/json",
            EnvelopeConstants.JsonContentType);
    }

    [Fact]
    public void matches_negative()
    {
        var list = new MimeTypeList("text/json,application/json");
        list.Matches("weird").ShouldBeFalse();
        list.Matches("weird", "wrong").ShouldBeFalse();
    }

    [Fact]
    public void matches_positive()
    {
        var list = new MimeTypeList("text/json,application/json");
        list.Matches("text/json").ShouldBeTrue();
        list.Matches(EnvelopeConstants.JsonContentType).ShouldBeTrue();
        list.Matches("text/json", EnvelopeConstants.JsonContentType).ShouldBeTrue();
    }

    [Fact]
    public void should_ignore_empty_string()
    {
        var list = new MimeTypeList(string.Empty);
        list.Count().ShouldBe(0);
    }

    [Fact]
    public void should_ignore_whitespace_only_string()
    {
        var list = new MimeTypeList("    ");
        list.Count().ShouldBe(0);
    }
}