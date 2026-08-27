using System.Text;

namespace Wolverine.Util;

internal static class SourceCodeExtensions
{
    /// <summary>
    /// This string as a C# string literal, quotes and escapes included, for embedding a value in
    /// generated code.
    /// <para>
    /// JasperFx's <c>Constant.For(string)</c> and <c>Constant.ForString(string)</c> only wrap the
    /// value in quotes, so any string reaching generated code from application configuration — an
    /// <c>[Entity(MissingMessage = "...")]</c>, say — has to come through here instead. A quote, a
    /// backslash or a newline in such a value would otherwise emit source that does not compile.
    /// </para>
    /// </summary>
    public static string ToStringLiteral(this string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                default:
                    // Anything else the C# lexer will not take verbatim inside a literal: other
                    // control characters, plus NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR, which it
                    // also treats as line breaks. Compared by code point so this line needs no
                    // escapes of its own.
                    if (char.IsControl(c) || c == 0x0085 || c == 0x2028 || c == 0x2029)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
