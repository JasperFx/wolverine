using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace Wolverine.Oracle.Util;

public static class OracleCommandExtensions
{
    /// <summary>
    /// Adds envelope IDs as individual parameters for use in an IN clause.
    /// Oracle uses :param syntax. Returns the parameter placeholder string (e.g., ":id0, :id1, :id2")
    /// </summary>
    internal static string WithEnvelopeIds(this DbCommand command, string name, Envelope[] envelopes)
    {
        if (envelopes.Length == 0)
        {
            return "NULL";
        }

        var placeholders = new string[envelopes.Length];
        for (var i = 0; i < envelopes.Length; i++)
        {
            var paramName = $"{name}_{i}";
            placeholders[i] = $":{paramName}";

            var parameter = command.CreateParameter();
            parameter.ParameterName = paramName;
            parameter.Value = envelopes[i].Id.ToByteArray();
            command.Parameters.Add(parameter);
        }

        return string.Join(", ", placeholders);
    }

    /// <summary>
    /// Adds GUID IDs as individual parameters for use in an IN clause.
    /// Returns the parameter placeholder string (e.g., ":id0, :id1, :id2")
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
            var paramName = $"{name}_{i}";
            placeholders[i] = $":{paramName}";

            var parameter = command.CreateParameter();
            parameter.ParameterName = paramName;
            parameter.Value = ids[i].ToByteArray();
            command.Parameters.Add(parameter);
        }

        return string.Join(", ", placeholders);
    }

    /// <summary>
    /// Execute a reader and map each row to T.
    /// </summary>
    internal static async Task<IReadOnlyList<T>> FetchListAsync<T>(this OracleCommand command,
        Func<DbDataReader, Task<T>> transform,
        CancellationToken cancellation = default)
    {
        var list = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            list.Add(await transform(reader));
        }

        return list;
    }
}
