using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wolverine.EntityFrameworkCore;

public static class CompensatingSagaMapping
{
    // EF Core's own requirement on an entity type
    private const DynamicallyAccessedMemberTypes EntityMembers =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields |
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties |
        DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>
    /// GH-5556. Map the members a <see cref="CompensatingSaga" /> adds: <see cref="CompensatingSaga.Status" /> as its
    /// name, and <see cref="CompensatingSaga.CompensationLog" /> as JSON in one text column. Call it in the saga's
    /// entity mapping; without it, EF Core cannot map the compensation log
    /// </summary>
    public static EntityTypeBuilder<TSaga> MapCompensatingSaga<[DynamicallyAccessedMembers(EntityMembers)] TSaga>(
        this EntityTypeBuilder<TSaga> builder)
        where TSaga : CompensatingSaga
    {
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50);

        builder.Property(x => x.CompensationLog)
            .HasConversion(
                log => JsonSerializer.Serialize(log, CompensationLogJsonContext.Default.ListCompensationLogEntry),
                json => JsonSerializer.Deserialize(json, CompensationLogJsonContext.Default.ListCompensationLogEntry) ??
                        new List<CompensationLogEntry>(),
                new ValueComparer<List<CompensationLogEntry>>(
                    (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                    log => log.Aggregate(0, (hash, entry) => HashCode.Combine(hash, entry.GetHashCode())),
                    log => log.ToList()));

        return builder;
    }
}

[JsonSerializable(typeof(List<CompensationLogEntry>))]
internal partial class CompensationLogJsonContext : JsonSerializerContext;
