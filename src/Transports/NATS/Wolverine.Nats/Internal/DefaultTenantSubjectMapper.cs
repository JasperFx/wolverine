namespace Wolverine.Nats.Internal;

/// <summary>
/// Default implementation of ITenantSubjectMapper that uses a prefix-based approach.
/// Maps subjects like "orders.created" to "tenant1.orders.created" for tenant "tenant1".
/// </summary>
public class DefaultTenantSubjectMapper : ITenantSubjectMapper
{
    private readonly string _separator;
    private readonly bool _normalizeSlashes;
    
    public DefaultTenantSubjectMapper(string separator = ".", bool normalizeSlashes = true)
    {
        _separator = separator;
        _normalizeSlashes = normalizeSlashes;
    }
    
    public string MapSubject(string baseSubject, string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
            return baseSubject;

        var normalizedTenantId = _normalizeSlashes
            ? tenantId.Replace('/', _separator[0])
            : tenantId;

        return $"{normalizedTenantId}{_separator}{baseSubject}";
    }
    
    public string? ExtractTenantId(string subject)
    {
        if (string.IsNullOrEmpty(subject))
            return null;

        var firstSeparatorIndex = subject.IndexOf(_separator, StringComparison.Ordinal);
        if (firstSeparatorIndex <= 0)
            return null;

        var tenantId = subject.Substring(0, firstSeparatorIndex);

        return _normalizeSlashes
            ? tenantId.Replace(_separator[0], '/')
            : tenantId;
    }
    
    public string GetSubscriptionPattern(string baseSubject)
    {
        return $"*{_separator}{baseSubject}";
    }
}