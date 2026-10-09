namespace Wolverine.Nats;

/// <summary>
/// A tenant subject mapper that leaves the subject alone: every tenant uses the plain subject. Meant for tenants on
/// their own connection (<c>AddTenant(tenantId, new SameSubjectTenantMapper(), cfg =&gt; ...)</c>), whose server or
/// account already keeps them apart, so their producers can publish to the same subjects as everyone else. The
/// tenant id of an incoming message then comes from the connection it arrived on.
/// </summary>
public class SameSubjectTenantMapper : ITenantSubjectMapper
{
    public string MapSubject(string baseSubject, string tenantId) => baseSubject;

    public string? ExtractTenantId(string subject) => null;

    public string GetSubscriptionPattern(string baseSubject) => baseSubject;
}
