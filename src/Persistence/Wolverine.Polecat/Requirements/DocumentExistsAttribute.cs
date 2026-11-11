using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.DocumentStores;
using Wolverine.Polecat.Persistence.Sagas;

namespace Wolverine.Polecat.Requirements;

/// <summary>
/// Apply to a handler or HTTP endpoint method to declaratively check that a Polecat document
/// of type <typeparamref name="TDoc"/> exists before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document is not found.
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
public class DocumentExistsAttribute<TDoc> : ModifyChainAttribute where TDoc : class
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
        DocumentExistenceRequirement.Apply(chain, container, new PolecatPersistenceFrameProvider(),
            PolecatIntegration.SessionTypes, typeof(TDoc), _argumentName, mustExist: true);
    }
}

/// <summary>
/// Apply to a handler or HTTP endpoint method to declaratively check that a Polecat document
/// of type <typeparamref name="TDoc"/> does NOT exist before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document already exists.
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
public class DocumentDoesNotExistAttribute<TDoc> : ModifyChainAttribute where TDoc : class
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
        DocumentExistenceRequirement.Apply(chain, container, new PolecatPersistenceFrameProvider(),
            PolecatIntegration.SessionTypes, typeof(TDoc), _argumentName, mustExist: false);
    }
}
