using System.Data.Common;
using MySqlConnector;

namespace Wolverine.MySql.Util;

public static class MySqlCommandExtensions
{
    /// <summary>
    /// Adds envelope IDs as individual parameters for use in an IN clause.
    /// MySQL doesn't support array parameters, so we create individual parameters.
    /// Returns the parameter placeholder string for use in SQL (e.g., "@p0, @p1, @p2")
    /// </summary>
    internal static string WithEnvelopeIds(this DbCommand command, string name, Envelope[] envelopes)
    {
        if (envelopes.Length == 0)
        {
            return "NULL"; // Return something that won't match any ID
        }

        var placeholders = new string[envelopes.Length];
        for (var i = 0; i < envelopes.Length; i++)
        {
            var paramName = $"@{name}_{i}";
            placeholders[i] = paramName;

            var parameter = command.CreateParameter();
            parameter.ParameterName = paramName;
            parameter.Value = envelopes[i].Id;
            command.Parameters.Add(parameter);
        }

        return string.Join(", ", placeholders);
    }

    /// <summary>
    /// Adds GUID IDs as individual parameters for use in an IN clause.
    /// Returns the parameter placeholder string for use in SQL (e.g., "@p0, @p1, @p2")
    /// </summary>
    internal static string WithIdList(this DbCommand command, string name, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return "NULL";
        }

        var placeholders = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var paramName = $"@{name}_{i}";
            placeholders[i] = paramName;

            var parameter = command.CreateParameter();
            parameter.ParameterName = paramName;
            parameter.Value = ids[i];
            command.Parameters.Add(parameter);
        }

        return string.Join(", ", placeholders);
    }
}
