using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;

namespace Wolverine.Marten;

/// <summary>
///     Load this parameter as a Marten document by its identity, exactly as <see cref="EntityAttribute" /> does,
///     but always through Marten rather than through whichever registered persistence provider happens to claim
///     the type first.
/// </summary>
/// <remarks>
///     <para>
///         Every <see cref="EntityAttribute" /> option applies unchanged —
///         <see cref="EntityAttribute.Required" />, <see cref="EntityAttribute.OnMissing" />,
///         <see cref="EntityAttribute.MissingMessage" />, <see cref="EntityAttribute.MaybeSoftDeleted" />, the
///         <c>ArgumentName</c> constructor and the <c>FromRoute</c> / <c>FromHeader</c> / <c>FromClaim</c> /
///         <c>FromMethod</c> value sources — because this attribute inherits the one implementation of them all.
///     </para>
///     <para>
///         Reach for it when you want the store named in the code rather than inferred, and especially when a type
///         could plausibly be claimed by two registered stores: Marten's provider claims every document type, so in
///         an application that also maps a type in an EF Core <c>DbContext</c>, a plain <c>[Entity]</c> resolves to
///         EF Core. <c>[FromMarten]</c> says which one you meant, and fails loudly if Marten cannot deliver.
///     </para>
/// </remarks>
public class FromMartenAttribute : ExplicitEntityAttribute
{
    public FromMartenAttribute()
    {
    }

    /// <param name="argumentName">
    ///     The name of the member on the incoming message, route argument, header, or claim that carries the
    ///     document's identity, when it is not <c>Id</c> or <c>{DocumentType}Id</c>.
    /// </param>
    public FromMartenAttribute(string argumentName) : base(argumentName)
    {
    }

    protected override Type providerType => typeof(MartenPersistenceFrameProvider);

    protected override string toolName => "Marten";

    protected override string integrationCall => "services.AddMarten(/* ... */).IntegrateWithWolverine()";

    // Marten's CanPersist claims every type -- it genuinely can persist any document -- so
    // ProviderCannotPersistType is unreachable here. The base message is kept anyway rather than
    // suppressed: if that ever stops being true, the diagnostic should still read correctly.
}
