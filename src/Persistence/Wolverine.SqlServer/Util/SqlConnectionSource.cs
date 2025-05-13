using System.Data.Common;
using JasperFx.CodeGeneration.Model;
using Microsoft.Data.SqlClient;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Util;

internal class SqlConnectionSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(SqlConnection) || type == typeof(DbConnection);
    }

    public Variable Create(Type type)
    {
        return type == typeof(DbConnection) 
            ? new ConnectionFrame<DbConnection>().ReturnVariable! 
            : new ConnectionFrame<SqlConnection>().ReturnVariable!;
    }
}