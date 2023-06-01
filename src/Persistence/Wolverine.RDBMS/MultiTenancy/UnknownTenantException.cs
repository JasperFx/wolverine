namespace Wolverine.RDBMS.MultiTenancy;

public class UnknownTenantException : Exception
{
    public UnknownTenantException(string tenantId) : base($"Unknown tenant id {tenantId}")
    {
    }
}