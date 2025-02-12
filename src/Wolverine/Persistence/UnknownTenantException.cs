namespace Wolverine.Persistence;

// TODO -- move to JasperFx
public class UnknownTenantException : Exception
{
    public UnknownTenantException(string tenantId) : base($"Unknown tenant id {tenantId}")
    {
    }
}