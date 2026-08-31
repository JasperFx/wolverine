using Wolverine.Util;
using Xunit;

namespace CoreTests.Util;

public class SourceCodeExtensionsTests
{
    [Theory]
    [InlineData("plain text", "\"plain text\"")]
    [InlineData("", "\"\"")]
    [InlineData("says \"hello\"", "\"says \\\"hello\\\"\"")]
    [InlineData("C:\\temp\\file", "\"C:\\\\temp\\\\file\"")]
    [InlineData("first\nsecond", "\"first\\nsecond\"")]
    [InlineData("first\r\nsecond", "\"first\\r\\nsecond\"")]
    [InlineData("before\tafter", "\"before\\tafter\"")]
    [InlineData("bell\a", "\"bell\\a\"")]
    [InlineData("null\0char", "\"null\\0char\"")]
    [InlineData("ctrl\u0001", "\"ctrl\\u0001\"")]
    [InlineData("next\u0085line", "\"next\\u0085line\"")]
    [InlineData("line\u2028separator", "\"line\\u2028separator\"")]
    // Neither a brace nor a non-ASCII character needs anything doing to it.
    [InlineData("still {0} a format string", "\"still {0} a format string\"")]
    [InlineData("em dash \u2014 stays", "\"em dash \u2014 stays\"")]
    public void escape_for_a_csharp_literal(string value, string expected)
    {
        value.ToStringLiteral().ShouldBe(expected);
    }
}
