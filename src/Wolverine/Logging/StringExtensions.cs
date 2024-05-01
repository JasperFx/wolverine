using JasperFx.Core;

namespace Wolverine.Logging;

internal static class StringExtensions
{
    internal static string ToTelemetryFriendly(this string s) => s.SplitPascalCase().Replace(' ', '.').ToLowerInvariant();
}
