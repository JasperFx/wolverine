using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.DocumentStores;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence.Requirements;

/// <summary>
/// Apply to a handler or HTTP endpoint method to declaratively check that a document of type
/// <typeparamref name="TDoc"/> exists before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document is not found.
///
/// Works with Marten, Polecat and Fisher. The store is resolved the way <see cref="StorageAttribute"/> resolves it:
/// a chain routed to an ancillary store (<c>[Storage]</c>, <c>[MartenStore]</c>, ...) checks that store, otherwise
/// the one document store integrated with Wolverine is used. With more than one document store integrated, route the
/// chain with <c>[Storage]</c>; the attribute refuses at code generation rather than guess.
///
/// The identity is resolved from the message/request by looking for a property named
/// <c>{DocTypeName}Id</c> or <c>Id</c> on the input type. You can override this by
/// specifying the property name explicitly in the attribute constructor.
/// </summary>
/// <example>
/// <code>
/// // Convention: looks for UserId or Id on the command
/// [DocumentExists&lt;User&gt;]
/// public void Handle(PromoteUser command) { }
///
/// // Explicit property name
/// [DocumentExists&lt;User&gt;(nameof(AddUser.UserId))]
/// public void Handle(AddUser command) { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class DocumentExistsAttribute<TDoc> : ModifyChainAttribute, IDocumentExistenceAttribute where TDoc : class
{
    private readonly string? _argumentName;

    public DocumentExistsAttribute()
    {
    }

    public DocumentExistsAttribute(string argumentName)
    {
        _argumentName = argumentName;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var provider = DocumentStoreResolution.Resolve(chain, rules, container, typeof(TDoc), "[DocumentExists]");
        DocumentExistenceRequirement.Apply(chain, container, provider, provider.SessionTypes, typeof(TDoc),
            _argumentName, mustExist: true);
    }
}

/// <summary>
/// Apply to a handler or HTTP endpoint method to declaratively check that a document of type
/// <typeparamref name="TDoc"/> does NOT exist before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document already exists.
///
/// Works with Marten, Polecat and Fisher. The store is resolved the way <see cref="StorageAttribute"/> resolves it:
/// a chain routed to an ancillary store (<c>[Storage]</c>, <c>[MartenStore]</c>, ...) checks that store, otherwise
/// the one document store integrated with Wolverine is used. With more than one document store integrated, route the
/// chain with <c>[Storage]</c>; the attribute refuses at code generation rather than guess.
///
/// The identity is resolved from the message/request by looking for a property named
/// <c>{DocTypeName}Id</c> or <c>Id</c> on the input type. You can override this by
/// specifying the property name explicitly in the attribute constructor.
/// </summary>
/// <example>
/// <code>
/// // Convention: looks for UserId or Id on the command
/// [DocumentDoesNotExist&lt;User&gt;]
/// public void Handle(CreateUser command) { }
///
/// // Explicit property name
/// [DocumentDoesNotExist&lt;User&gt;(nameof(CreateUser.Email))]
/// public void Handle(CreateUser command) { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class DocumentDoesNotExistAttribute<TDoc> : ModifyChainAttribute, IDocumentExistenceAttribute where TDoc : class
{
    private readonly string? _argumentName;

    public DocumentDoesNotExistAttribute()
    {
    }

    public DocumentDoesNotExistAttribute(string argumentName)
    {
        _argumentName = argumentName;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var provider =
            DocumentStoreResolution.Resolve(chain, rules, container, typeof(TDoc), "[DocumentDoesNotExist]");
        DocumentExistenceRequirement.Apply(chain, container, provider, provider.SessionTypes, typeof(TDoc),
            _argumentName, mustExist: false);
    }
}

/// <summary>
///     GH-5514: finds the document store integration a store-agnostic attribute should act through.
/// </summary>
internal static class DocumentStoreResolution
{
    public static IDocumentStoreFrameProvider Resolve(IChain chain, GenerationRules rules,
        IServiceContainer container, Type documentType, string attributeName)
    {
        var stores = rules.OrderedPersistenceProviders().OfType<IDocumentStoreFrameProvider>().ToList();

        if (stores.Count == 0)
        {
            throw new InvalidOperationException(
                $"{attributeName} on chain {chain} needs a document store integrated with Wolverine, and none is. " +
                "Call IntegrateWithWolverine() on your Marten, Polecat or Fisher configuration.");
        }

        var storeType = chain.DetermineAncillaryStoreType();
        if (storeType != null)
        {
            return stores.FirstOrDefault(x => x.OwnsStorageType(storeType, container))
                   ?? throw new InvalidOperationException(
                       $"{attributeName} on chain {chain}: no document store integrated with Wolverine owns the store type {storeType.FullNameInCode()}.");
        }

        var candidates = stores.Where(x => x.CanPersist(documentType, container, out _)).ToList();
        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"{attributeName} on chain {chain}: no document store integrated with Wolverine can persist {documentType.FullNameInCode()}.");
        }

        throw new InvalidOperationException(
            $"{attributeName} on chain {chain} cannot tell which document store to check for {documentType.FullNameInCode()}: " +
            $"{candidates.Select(x => x.SessionTypes.StoreName).Join(", ")} are all integrated with Wolverine. " +
            "Route the chain to one store with [Storage(typeof(...))].");
    }
}
