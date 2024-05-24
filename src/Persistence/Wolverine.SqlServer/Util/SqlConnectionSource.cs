using System.Data.Common;
using JasperFx.CodeGeneration.Model;
using Microsoft.Data.SqlClient;

namespace Wolverine.SqlServer.Util;

internal class SqlConnectionSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(SqlConnection) || type == typeof(DbConnection);
    }

    public Variable Create(Type type)
    {
        return new SqlConnectionFrame(type).Connection;
    }
}