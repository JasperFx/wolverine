using System;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Runtime.Handlers;

internal class ContainerVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IContainer) || type == typeof(IServiceProvider);
    }

    public Variable Create(Type type)
    {
        throw new NotImplementedException();
    }
}