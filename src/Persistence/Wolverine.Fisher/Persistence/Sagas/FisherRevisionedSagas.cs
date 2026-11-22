using JasperFx;
using JasperFx.Core.Reflection;
using Fisher;

namespace Wolverine.Fisher.Persistence.Sagas;

internal static class FisherRevisionedSagas
{
    /// <summary>
    ///     GH-5658: map an <see cref="IRevisioned" /> saga's revision onto <see cref="Saga.Version" />, so a saga loaded
    ///     from Fisher carries the revision the update frame builds on. Fisher maps <see cref="IRevisioned.Version" />
    ///     itself only when the document type declares the property, and a saga inherits it from <see cref="Saga" />,
    ///     so without this every loaded saga reads revision 0 and its next update fails the revision check.
    /// </summary>
    public static void Configure(StoreOptions options)
    {
        options.Policies.ForAllDocuments(mapping =>
        {
            if (!mapping.DocumentType.CanBeCastTo<Saga>() || !mapping.DocumentType.CanBeCastTo<IRevisioned>()) return;
            if (mapping.Metadata.Revision.Member != null) return;

            mapping.Metadata.Revision.MapTo(mapping.DocumentType.GetProperty(nameof(Saga.Version), typeof(int))!);
        });
    }
}
