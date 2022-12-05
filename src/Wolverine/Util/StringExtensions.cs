using System;
using System.Linq;
using JasperFx.Core;

namespace Wolverine.Util;

public static class StringExtensions
{
    public static Uri ToUri(this string uriString)
    {
        if (uriString.Contains("://*"))
        {
            var parts = uriString.Split(':');

            var protocol = parts[0];
            var segments = parts[2].Split('/');
            var port = int.Parse(segments.First());

            var uri = $"{protocol}://localhost:{port}/{segments.Skip(1).Join("/")}";
            return new Uri(uri);
        }

        if (uriString.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(uriString), $"'{uriString}' is not a valid Uri");
        }

        return new Uri(uriString);
    }

    public static bool IsIn(this string text, params string[] values)
    {
        return values.Contains(text);
    }

    // Taken from https://andrewlock.net/why-is-string-gethashcode-different-each-time-i-run-my-program-in-net-core/#a-deterministic-gethashcode-implementation
    internal static int GetDeterministicHashCode(this string stringValue)
    {
        unchecked
        {
            var hash1 = (5381 << 16) + 5381;
            var hash2 = hash1;

            for (var i = 0; i < stringValue.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ stringValue[i];
                if (i == stringValue.Length - 1)
                {
                    break;
                }

                hash2 = ((hash2 << 5) + hash2) ^ stringValue[i + 1];
            }

            return hash1 + hash2 * 1566083941;
        }
    }
}