using System;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.RDBMS;

public class DatabaseBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == GetType();
    }

    public Variable Create(Type type)
    {
        return Variable.For<IDatabaseBackedMessageStore>();
    }
}