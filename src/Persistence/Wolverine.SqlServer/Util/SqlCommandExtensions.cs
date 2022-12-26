using System.Data;
using System.Data.Common;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Util;

internal static class SqlCommandExtensions
{
    public static DbCommand WithIdList(this DbCommand cmd, DatabaseSettings settings, IReadOnlyList<Envelope> envelopes,
        string parameterName = "IDLIST")
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        foreach (var envelope in envelopes) table.Rows.Add(envelope.Id);

        var parameter = cmd.CreateParameter().As<SqlParameter>();
        parameter.ParameterName = parameterName;
        parameter.Value = table;

        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = $"{settings.SchemaName}.EnvelopeIdList";

        cmd.Parameters.Add(parameter);

        return cmd;
    }
}