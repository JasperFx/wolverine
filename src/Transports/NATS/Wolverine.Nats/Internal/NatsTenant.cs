namespace Wolverine.Nats.Internal;

public class NatsTenant
{
    public NatsTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
    }

    public string TenantId { get; }
    public ITenantSubjectMapper? SubjectMapper { get; set; }
    public string? ConnectionString { get; set; }
    public string? CredentialsFile { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }
}
