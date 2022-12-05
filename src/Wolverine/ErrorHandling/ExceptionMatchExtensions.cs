using Wolverine.ErrorHandling.Matches;

namespace Wolverine.ErrorHandling;

internal static class ExceptionMatchExtensions
{
    public static string Formatted(this IExceptionMatch match)
    {
        return match switch
        {
            AndMatch andMatch when andMatch.Inners.Count > 1 => $"({match.Formatted()})",
            OrMatch orMatch when orMatch.Inners.Count > 1 => $"({match.Formatted()})",
            _ => match.Description
        };
    }

    public static IExceptionMatch Or(this IExceptionMatch match, IExceptionMatch other)
    {
        if (match is OrMatch or)
        {
            or.Inners.Add(other);
            return or;
        }

        return new OrMatch(match, other);
    }

    public static IExceptionMatch And(this IExceptionMatch match, IExceptionMatch other)
    {
        if (match is AndMatch and)
        {
            and.Inners.Add(other);
            return and;
        }

        return new AndMatch(match, other);
    }
}