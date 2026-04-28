using System.Globalization;

namespace Wolverine.Persistence;

/// <summary>
/// An opaque reference to a payload stored in an <see cref="IClaimCheckStore"/>.
/// </summary>
/// <param name="Id">
/// Backend-specific identifier (typically a blob name / object key).
/// </param>
/// <param name="ContentType">MIME content type of the stored payload.</param>
/// <param name="Length">Size of the stored payload in bytes.</param>
public record ClaimCheckToken(string Id, string ContentType, long Length)
{
    private const char Separator = '|';

    /// <summary>
    /// Encode this token into the single-line wire format used inside Wolverine
    /// envelope headers: <c>{id}|{contentType}|{length}</c>. Pipe characters in the
    /// id or content type are URL-encoded so the format remains round-trippable.
    /// </summary>
    public string Serialize()
    {
        return string.Concat(
            Escape(Id),
            Separator,
            Escape(ContentType),
            Separator,
            Length.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Parse a token previously produced by <see cref="Serialize"/>.
    /// </summary>
    /// <exception cref="FormatException">Thrown if <paramref name="value"/> is not a recognised token format.</exception>
    public static ClaimCheckToken Parse(string value)
    {
        if (TryParse(value, out var token))
        {
            return token;
        }

        throw new FormatException($"'{value}' is not a valid ClaimCheckToken header value.");
    }

    /// <summary>
    /// Attempt to parse a token previously produced by <see cref="Serialize"/>.
    /// </summary>
    public static bool TryParse(string? value, out ClaimCheckToken token)
    {
        token = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(Separator);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return false;
        }

        token = new ClaimCheckToken(Unescape(parts[0]), Unescape(parts[1]), length);
        return true;
    }

    private static string Escape(string value) => value.Replace("%", "%25").Replace("|", "%7C");

    private static string Unescape(string value) => value.Replace("%7C", "|").Replace("%25", "%");
}

