using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

public class NoMatchingPersistenceProviderException : Exception
{
    public NoMatchingPersistenceProviderException(Type entityType) : base($"Wolverine is unable to determine a persistence provider for entity type {entityType.FullNameInCode()}")
    {
    }
}