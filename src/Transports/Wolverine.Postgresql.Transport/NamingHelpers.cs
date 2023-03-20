using System.Text.RegularExpressions;

namespace Wolverine.Transports.Postgresql;

internal static class NamingHelpers
{
    public static string GetQueueName(string name) => $"queue_{name}".ToLower().VerifyString();

    public static string GetQueueChannelName(string name)
        => $"queue_{name}_channel".ToLower().VerifyString();

    public static string GetTriggerName(string name)
        => $"notify_{name}_insert".ToLower().VerifyString();

    public static string GetTriggerFunctionName(string name)
        => $"notify_{name}".ToLower().VerifyString();

    /// <summary>
    /// Expectes a string that contains a fullname of a class (namespace). Split the string on the dot
    /// and returns as many parts as fit in 40 characters. Right to left.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static string SanitizeQueueName(string name)
    {
        const int maxValidIndexSize = 59;
        const int prefixLength = 6;
        const int suffixLength = 19;
        const int totalAllowedLength = maxValidIndexSize - prefixLength - suffixLength;

        var parts = name.Split(".", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var length = 0;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (length + part.Length > totalAllowedLength)
            {
                break;
            }

            result.Add(part);
            length += part.Length + 1;
        }

        result.Reverse();
        return string.Join("_", result).VerifyString();
    }

    private static readonly Regex _verifyRegex = new(@"[^a-z0-9_]*", RegexOptions.Compiled);

    private static string VerifyString(this string str)
    {
        if (!_verifyRegex.IsMatch(str))
        {
            throw new ArgumentException(
                $"The string '{str}' contains invalid characters. Only a-z, 0-9 and _ are allowed.");
        }

        return str;
    }
}
