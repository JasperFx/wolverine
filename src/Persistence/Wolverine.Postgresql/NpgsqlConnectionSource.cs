using JasperFx.CodeGeneration.Model;
using Npgsql;
using System.Data.Common;

namespace Wolverine.Postgresql;

internal class NpgsqlConnectionSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(NpgsqlConnection) || type == typeof(DbConnection);
    }

    public Variable Create(Type type)
    {
        return new NpgsqlConnectionFrame(type).Connection;
    }
}