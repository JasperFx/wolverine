using JasperFx.CodeGeneration.Model;
using Wolverine.Sqlite;

namespace Wolverine.Fisher;

internal class FisherBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<SqliteMessageStore>();
    }
}
