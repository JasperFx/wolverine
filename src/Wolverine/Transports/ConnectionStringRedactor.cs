using System.Text;

namespace Wolverine.Transports;

/// <summary>
/// Small helper for masking credential-bearing keys in a connection string
/// while preserving the rest of the content. Used to render a transport's
/// connection string in <see cref="JasperFx.Descriptors.OptionsDescription"/>
/// output without leaking secrets into logs or diagnostic UIs.
/// </summary>
public static class ConnectionStringRedactor
{
    /// <summary>
    /// Mask the values of any <c>key=value</c> segments whose key (case-insensitive)
    /// appears in <paramref name="secretKeys"/>. Segments are delimited by
    /// <c>;</c>. Unknown keys pass through unchanged.
    /// </summary>
    public static string Redact(string? connectionString, params string[] secretKeys)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return string.Empty;
        if (secretKeys == null || secretKeys.Length == 0) return connectionString!;

        var builder = new StringBuilder(connectionString!.Length);
        var segments = connectionString.Split(';');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0) continue;

            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex > 0)
            {
                var key = segment.Substring(0, equalsIndex);
                if (IsSecretKey(key, secretKeys))
                {
                    if (builder.Length > 0) builder.Append(';');
                    builder.Append(key).Append("=****");
                    continue;
                }
            }

            if (builder.Length > 0) builder.Append(';');
            builder.Append(segment);
        }

        return builder.ToString();
    }

    private static bool IsSecretKey(string key, string[] secretKeys)
    {
        foreach (var secret in secretKeys)
        {
            if (string.Equals(key.Trim(), secret, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
