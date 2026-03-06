using JasperFx.CodeGeneration.Model;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.Polecat;

internal class PolecatBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<SqlServerMessageStore>();
    }
}
