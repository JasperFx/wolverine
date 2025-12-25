namespace Wolverine.Nats;

/// <summary>
/// Interface for mapping tenant IDs to NATS subjects and extracting tenant IDs from subjects.
/// Implement this interface to provide custom tenant-subject mapping strategies.
/// </summary>
public interface ITenantSubjectMapper
{
    /// <summary>
    /// Maps a base subject to a tenant-specific subject.
    /// </summary>
    /// <param name="baseSubject">The base subject without tenant information</param>
    /// <param name="tenantId">The tenant ID to incorporate into the subject</param>
    /// <returns>The tenant-specific subject</returns>
    string MapSubject(string baseSubject, string tenantId);
    
    /// <summary>
    /// Extracts the tenant ID from a NATS subject.
    /// </summary>
    /// <param name="subject">The full NATS subject that may contain tenant information</param>
    /// <returns>The tenant ID if found, otherwise null</returns>
    string? ExtractTenantId(string subject);
    
    /// <summary>
    /// Gets the subscription pattern for listening to all tenant subjects for a given base subject.
    /// For example, if using prefix-based mapping, this might return "*.orders.>" for base subject "orders.>"
    /// </summary>
    /// <param name="baseSubject">The base subject pattern</param>
    /// <returns>The subscription pattern that matches all tenant variants</returns>
    string GetSubscriptionPattern(string baseSubject);
}