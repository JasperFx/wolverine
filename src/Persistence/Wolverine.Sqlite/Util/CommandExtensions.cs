using System.Data.Common;
using JasperFx.Core.Reflection;
using Microsoft.Data.Sqlite;

namespace Wolverine.Sqlite.Util;

public static class CommandExtensions
{
    internal static DbCommand WithEnvelopeIds(this DbCommand command, string name, Envelope[] envelopes)
    {
        // SQLite doesn't support array types, so we'll use a comma-separated list
        var ids = string.Join(",", envelopes.Select(x => $"'{x.Id:D}'"));
        var parameter = command.CreateParameter().As<SqliteParameter>();
        parameter.ParameterName = name;
        parameter.Value = ids;
        command.Parameters.Add(parameter);

        return command;
    }
}
